using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace POE2Radar.Core.Game;

/// <summary>
/// Read-path helpers that cut syscalls without changing what is read: fewer, larger reads of contiguous
/// fields, and caches that are VALIDATED against the raw bytes they were derived from (so a changed or
/// recycled structure is re-read, never served stale). Every helper keeps the original per-field path as a
/// fallback for the case where the combined read fails, so failure semantics match the old code exactly.
/// </summary>
public sealed partial class Poe2Live
{
    // ── std::wstring (MSVC layout: 16-byte SSO buffer / heap ptr @0, size @0x10, capacity @0x18) ──

    private const int StdWStringSize = 0x20;

    /// <summary>Read a std::wstring. The 32-byte header is fetched in ONE read (was: size, then pointer, then
    /// the chars — 3 syscalls for a heap string, 2 for an inline one). Falls back to the field-by-field read
    /// if the header can't be read in one go.</summary>
    internal string ReadStdWString(nint addr)
    {
        Span<byte> h = stackalloc byte[StdWStringSize];
        if (_reader.TryReadBytes(addr, h) != StdWStringSize) return ReadStdWStringSlow(addr);
        return DecodeStdWString(h);
    }

    /// <summary>The original field-by-field std::wstring read (reference semantics + fallback).</summary>
    internal string ReadStdWStringSlow(nint addr)
    {
        if (!_reader.TryReadStruct<int>(addr + 0x10, out var len) || len <= 0 || len > 1024) return string.Empty;
        if (len < 8) return _reader.ReadStringUtf16(addr, len);
        var ptr = Ptr(addr);
        return ptr == 0 ? string.Empty : _reader.ReadStringUtf16(ptr, len);
    }

    /// <summary>Decode a std::wstring from its already-read header; same rules as <see cref="ReadStdWStringSlow"/>
    /// (inline chars up to the first NUL when size &lt; 8, else a validated heap pointer read of size chars).</summary>
    private string DecodeStdWString(ReadOnlySpan<byte> h)
    {
        var len = BinaryPrimitives.ReadInt32LittleEndian(h[0x10..]);
        if (len <= 0 || len > 1024) return string.Empty;
        if (len < 8)
        {
            var chars = MemoryMarshal.Cast<byte, char>(h[..(len * 2)]);
            var nul = chars.IndexOf('\0');
            return new string(nul >= 0 ? chars[..nul] : chars);
        }
        var ptr = PlausiblePtr(BinaryPrimitives.ReadInt64LittleEndian(h));
        return ptr == 0 ? string.Empty : _reader.ReadStringUtf16(ptr, len);
    }

    /// <summary>Allocation-free <c>(firstLine(ReadStdWString(addr))).Trim() == expected</c> (ordinal) — the loot-tag
    /// stale-element guard runs per tag per RENDER frame, where materializing the text + substring + trimmed
    /// copy allocated three strings per tag per frame. Same read rules as <see cref="ReadStdWString"/>.</summary>
    internal bool WStringFirstLineEquals(nint addr, string expected)
    {
        Span<byte> h = stackalloc byte[StdWStringSize];
        if (_reader.TryReadBytes(addr, h) != StdWStringSize)
            return FirstLineEquals(ReadStdWStringSlow(addr), expected);
        var len = BinaryPrimitives.ReadInt32LittleEndian(h[0x10..]);
        if (len <= 0 || len > 1024) return FirstLineEquals(ReadOnlySpan<char>.Empty, expected);
        scoped ReadOnlySpan<char> text;
        Span<char> heap = stackalloc char[len < 8 ? 0 : len];
        if (len < 8) text = MemoryMarshal.Cast<byte, char>(h[..(len * 2)]);
        else
        {
            var ptr = PlausiblePtr(BinaryPrimitives.ReadInt64LittleEndian(h));
            // ReadStringUtf16 is all-or-nothing: a failed read yields "" — mirror that.
            if (ptr == 0 || _reader.TryReadBytes(ptr, MemoryMarshal.AsBytes(heap)) != len * 2) text = ReadOnlySpan<char>.Empty;
            else text = heap;
        }
        var nul = text.IndexOf('\0');
        if (nul >= 0) text = text[..nul];
        return FirstLineEquals(text, expected);
    }

    private static bool FirstLineEquals(ReadOnlySpan<char> text, string expected)
    {
        var nl = text.IndexOf('\n');
        return (nl >= 0 ? text[..nl] : text).Trim().SequenceEqual(expected);
    }

    /// <summary>Same plausibility filter as <see cref="Ptr"/>, for pointers taken out of a bulk-read buffer.</summary>
    private static nint PlausiblePtr(long p)
    {
        var u = (ulong)p;
        return (u < 0x10000 || u > 0x7FFFFFFFFFFF) ? 0 : (nint)p;
    }

    // ── Entity metadata path, cached per EntityDetails (shared by every entity of one type) ──

    private readonly Dictionary<nint, (Int128 Lo, Int128 Hi, string Text)> _metaByDetails = new();

    /// <summary>Read an entity's metadata path: EntityDetails(+0x08) → name StdWString(+0x08). The string is
    /// cached per EntityDetails and served only while that std::wstring's 32-byte header (buffer/pointer,
    /// size, capacity) is byte-identical, so the per-tick <see cref="TryResolve"/> check and new-entity
    /// categorization cost one header read and no allocation.</summary>
    internal string ReadMetadata(nint entity)
    {
        var details = Ptr(entity + Poe2.Entity.EntityDetailsPtr);
        if (details == 0) return string.Empty;
        var addr = details + Poe2.EntityDetails.Name;
        Span<byte> h = stackalloc byte[StdWStringSize];
        if (_reader.TryReadBytes(addr, h) != StdWStringSize) return ReadStdWStringSlow(addr);
        var lo = MemoryMarshal.Read<Int128>(h);
        var hi = MemoryMarshal.Read<Int128>(h[16..]);
        if (_metaByDetails.TryGetValue(details, out var c) && c.Lo == lo && c.Hi == hi) return c.Text;
        var s = DecodeStdWString(h);
        if (s.Length > 0)   // never cache a failed/empty read
        {
            if (_metaByDetails.Count >= 8192) _metaByDetails.Clear();
            _metaByDetails[details] = (lo, hi, s);
        }
        return s;
    }

    // ── Component lookup: name → index map per ComponentLookUp, validated by the raw bucket bytes ──

    private sealed class LookupNameMap
    {
        public required byte[] Bucket;                        // the (NamePtr, Index) entries it was built from
        public required Dictionary<string, int[]> Indices;    // name → indices, in bucket order
    }

    private readonly Dictionary<nint, LookupNameMap> _lookupNames = new();

    private readonly byte[] _bucketBuf = new byte[256 * Poe2.ComponentLookUp.EntryStride];

    /// <summary>Test/diagnostic: number of cached ComponentLookUp name maps.</summary>
    internal int CachedLookupMaps => _lookupNames.Count;

    /// <summary>Resolve a component address by name via EntityDetails → ComponentLookUp (StdBucket) →
    /// ComponentList. The bucket is read in ONE read and turned into a name→index map that is cached per
    /// ComponentLookUp (every entity of a type shares one) and reused while the bucket bytes are identical —
    /// replacing the old 3 syscalls + 1 string allocation PER BUCKET ENTRY on every resolve (a monster type
    /// with ~30 components cost ~90 reads per missing component). Same first-match-in-bucket-order,
    /// index-in-range semantics as <see cref="ResolveComponentSlow"/>, which remains the fallback.</summary>
    internal nint ResolveComponent(nint entity, string name)
    {
        if (!TryComponentHeader(entity, out var lookup, out var compList, out var compCount, out var bFirst, out var entries))
            return 0;
        var map = LookupNames(lookup, bFirst, entries);
        if (map is null) return ScanBucket(bFirst, entries, compList, compCount, name);
        if (!map.Indices.TryGetValue(name, out var idxs)) return 0;
        foreach (var index in idxs)
            if (index >= 0 && index < compCount) return Ptr(compList.First + (nint)(index * 8));
        return 0;
    }

    /// <summary>The original per-entry resolve (reference semantics for tests + the fallback path).</summary>
    internal nint ResolveComponentSlow(nint entity, string name)
        => TryComponentHeader(entity, out _, out var compList, out var compCount, out var bFirst, out var entries)
            ? ScanBucket(bFirst, entries, compList, compCount, name) : 0;

    private bool TryComponentHeader(nint entity, out nint lookup, out StdVector compList, out long compCount,
        out nint bFirst, out int entries)
    {
        lookup = 0; compList = default; compCount = 0; bFirst = 0; entries = 0;
        var details = Ptr(entity + Poe2.Entity.EntityDetailsPtr);
        if (details == 0) return false;
        lookup = Ptr(details + Poe2.EntityDetails.ComponentLookUpPtr);
        if (lookup == 0) return false;
        if (!_reader.TryReadStruct<StdVector>(entity + Poe2.Entity.ComponentList, out compList)) return false;
        compCount = ((long)compList.Last - (long)compList.First) / 8;
        if (compCount is <= 0 or > 256) return false;

        bFirst = Ptr(lookup + Poe2.ComponentLookUp.NameAndIndexBucket);
        if (!_reader.TryReadStruct<nint>(lookup + Poe2.ComponentLookUp.NameAndIndexBucket + 8, out var bLast)) return false;
        var n = ((long)bLast - (long)bFirst) / Poe2.ComponentLookUp.EntryStride;
        if (bFirst == 0 || n is <= 0 or > 256) return false;
        entries = (int)n;
        return true;
    }

    private nint ScanBucket(nint bFirst, long entries, StdVector compList, long compCount, string name)
    {
        for (long i = 0; i < entries; i++)
        {
            var e = bFirst + (nint)(i * Poe2.ComponentLookUp.EntryStride);
            var namePtr = Ptr(e);
            if (!_reader.TryReadStruct<int>(e + 8, out var index)) continue;
            if (index < 0 || index >= compCount) continue;
            if (_reader.ReadStringUtf8(namePtr, 32) != name) continue;
            return Ptr(compList.First + (nint)(index * 8));
        }
        return 0;
    }

    /// <summary>The validated name map for a lookup's bucket, or null if the bucket can't be read in one go
    /// (caller falls back to the per-entry scan). A map whose name reads partially failed is used for this
    /// call only (failed names read as "", exactly like the per-entry path) and not cached.</summary>
    private LookupNameMap? LookupNames(nint lookup, nint bFirst, int entries)
    {
        const int stride = Poe2.ComponentLookUp.EntryStride;
        var bucket = _bucketBuf.AsSpan(0, entries * stride);
        if (_reader.TryReadBytes(bFirst, bucket) != bucket.Length) return null;
        if (_lookupNames.TryGetValue(lookup, out var cached) && bucket.SequenceEqual(cached.Bucket)) return cached;

        var lists = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var complete = true;
        Span<byte> nameBuf = stackalloc byte[32];
        for (var i = 0; i < entries; i++)
        {
            var entry = bucket.Slice(i * stride, stride);
            var namePtr = PlausiblePtr(BinaryPrimitives.ReadInt64LittleEndian(entry));
            var index = BinaryPrimitives.ReadInt32LittleEndian(entry[8..]);
            string entryName;
            if (namePtr == 0) entryName = string.Empty;   // ReadStringUtf8(0) → ""
            else if (_reader.TryReadBytes(namePtr, nameBuf) != nameBuf.Length) { entryName = string.Empty; complete = false; }
            else
            {
                var nul = nameBuf.IndexOf((byte)0);
                entryName = Encoding.UTF8.GetString(nul >= 0 ? nameBuf[..nul] : nameBuf);
            }
            if (!lists.TryGetValue(entryName, out var l)) lists[entryName] = l = new List<int>(1);
            l.Add(index);
        }
        var map = new LookupNameMap
        {
            Bucket = bucket.ToArray(),
            Indices = lists.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal),
        };
        if (complete)
        {
            if (_lookupNames.Count >= 2048) _lookupNames.Clear();
            _lookupNames[lookup] = map;
        }
        return map;
    }
}
