using System.Reflection;
using System.Runtime.InteropServices;
using POE2Radar.Core;
using POE2Radar.Core.Game;
using Xunit;
using Xunit.Abstractions;

namespace POE2Radar.Tests;

/// <summary>Reads a synthetic UI tree in this test process through the real OS memory reader.</summary>
public sealed class HoverReaderTests(ITestOutputHelper output)
{
    [Fact]
    public void Cached_slot_reduces_reads_and_revalidates_stack_visibility_and_pointer()
    {
        using var memory = new Fixture();
        using var process = OperatingSystem.IsLinux()
            ? (ProcessHandle)typeof(ProcessHandle).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
                .Invoke([Environment.ProcessId, "self-test", "", (nint)0, (uint)0, (nint)Environment.ProcessId])
            : ProcessHandle.AttachToProcess(Environment.ProcessId);
        var reader = new MemoryReader(process);
        var live = new Poe2Live(reader, 0);
        var first = live.ReadHoveredItem(memory.State, 1280, 800, 20, 20);
        Assert.NotNull(first);
        Assert.Equal(3, first.Value.Stack);
        var fullReads = live.HoverScanReads;
        Assert.Equal(1002, live.HoverScanNodes);

        // Update the live stack in place. The cache retains the slot, not an old item snapshot.
        Marshal.WriteInt32(memory.Stack + Poe2.StackComponent.Count, 9);
        var second = live.ReadHoveredItem(memory.State, 1280, 800, 20, 20);
        Assert.Equal(9, second!.Value.Stack);
        Assert.Equal(1, live.HoverCacheHits);
        Assert.Equal(0, live.HoverScanNodes);
        var cachedReads = live.HoverScanReads;
        output.WriteLine($"Full UI scan: {fullReads} OS reads; cached hover: {cachedReads} OS reads");
        Assert.True(cachedReads < fullReads / 4);

        // Closing an ancestor must invalidate a locally-visible cached child immediately.
        Marshal.WriteInt32(memory.Panel + Poe2.UiElement.Flags, 0);
        Assert.Null(live.ReadHoveredItem(memory.State, 1280, 800, 20, 20));
        Marshal.WriteInt32(memory.Panel + Poe2.UiElement.Flags, 1 << Poe2.UiElement.FlagVisibleBit);
        Assert.NotNull(live.ReadHoveredItem(memory.State, 1280, 800, 20, 20));
        // Slot remains visible but its item has been removed.
        Marshal.WriteIntPtr(memory.Slot + Poe2.Ritual.TileSlotItem, 0);
        Assert.Null(live.ReadHoveredItem(memory.State, 1280, 800, 20, 20));
    }

    [Theory]
    [InlineData(0x4F8)]
    [InlineData(0x3A0)]
    public void Inventory_descriptor_resolves_actual_item(int widgetOffset)
    {
        using var memory = new Fixture();
        using var process = OperatingSystem.IsLinux()
            ? (ProcessHandle)typeof(ProcessHandle).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
                .Invoke([Environment.ProcessId, "self-test", "", (nint)0, (uint)0, (nint)Environment.ProcessId])
            : ProcessHandle.AttachToProcess(Environment.ProcessId);
        var descriptor = Marshal.AllocHGlobal(0x800);
        try
        {
            Marshal.Copy(new byte[0x800], 0, descriptor, 0x800);
            Marshal.WriteIntPtr(descriptor, memory.Item);
            Marshal.WriteIntPtr(memory.Slot + Poe2.Ritual.TileSlotItem, 0);
            Marshal.WriteIntPtr(memory.Slot + widgetOffset, descriptor);
            var live = new Poe2Live(new MemoryReader(process), 0);
            var item = live.ReadHoveredItem(memory.State, 1280, 800, 20, 20);
            Assert.NotNull(item);
            Assert.Equal(memory.Item, item.Value.Item);
            Assert.Equal(3, item.Value.Stack);
            Assert.NotNull(live.ReadHoveredItem(memory.State, 1280, 800, 20, 20));
        }
        finally { Marshal.FreeHGlobal(descriptor); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly List<nint> _allocated = [];
        public nint State, Panel, Slot, Stack, Item;
        private nint Allocate(int size = 0x800)
        {
            var ptr = Marshal.AllocHGlobal(size);
            Marshal.Copy(new byte[size], 0, ptr, size);
            _allocated.Add(ptr);
            return ptr;
        }
        private nint Ui(nint parent)
        {
            var node = Allocate();
            Marshal.WriteIntPtr(node + Poe2.UiElement.Self, node);
            Marshal.WriteIntPtr(node + Poe2.UiElement.Parent, parent);
            Marshal.WriteInt32(node + Poe2.UiElement.Flags, 1 << Poe2.UiElement.FlagVisibleBit);
            Marshal.WriteInt32(node + Poe2.UiElement.SizeW, BitConverter.SingleToInt32Bits(48));
            Marshal.WriteInt32(node + Poe2.UiElement.SizeH, BitConverter.SingleToInt32Bits(48));
            return node;
        }
        public Fixture()
        {
            State = Allocate(); var root = Ui(0); Panel = Ui(root); Slot = Ui(Panel);
            Marshal.WriteIntPtr(State + Poe2.InGameState.UiRoot, root);
            Children(root, [Panel]);
            var siblings = new nint[1000]; siblings[0] = Slot;
            for (var i = 1; i < siblings.Length; i++) siblings[i] = Ui(Panel);
            Children(Panel, siblings);
            var item = Allocate(); Item = item; var details = Allocate(); var lookup = Allocate();
            var bucket = Allocate(); var components = Allocate(); var render = Allocate(); Stack = Allocate();
            Marshal.WriteIntPtr(Slot + Poe2.Ritual.TileSlotItem, item);
            Marshal.WriteIntPtr(item + Poe2.Entity.EntityDetailsPtr, details);
            Marshal.WriteIntPtr(details + Poe2.EntityDetails.ComponentLookUpPtr, lookup);
            var metadata = "Metadata/Items/Rings/TestRing";
            var metadataPtr = Allocate();
            var metadataBytes = System.Text.Encoding.Unicode.GetBytes(metadata);
            Marshal.Copy(metadataBytes, 0, metadataPtr, metadataBytes.Length);
            Marshal.WriteIntPtr(details + Poe2.EntityDetails.Name, metadataPtr);
            Marshal.WriteInt32(details + Poe2.EntityDetails.Name + 0x10, metadata.Length);
            Marshal.WriteIntPtr(item + Poe2.Entity.ComponentList, components);
            Marshal.WriteIntPtr(item + Poe2.Entity.ComponentList + 8, components + 16);
            Marshal.WriteIntPtr(components, render); Marshal.WriteIntPtr(components + 8, Stack);
            Marshal.WriteIntPtr(lookup + Poe2.ComponentLookUp.NameAndIndexBucket, bucket);
            Marshal.WriteIntPtr(lookup + Poe2.ComponentLookUp.NameAndIndexBucket + 8, bucket + 32);
            Component(bucket, "RenderItem", 0); Component(bucket + 16, "Stack", 1);
            Marshal.WriteInt32(Stack + Poe2.StackComponent.Count, 3);
        }
        private void Component(nint entry, string name, int index)
        {
            var ptr = Allocate(); var bytes = System.Text.Encoding.UTF8.GetBytes(name);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            Marshal.WriteIntPtr(entry, ptr); Marshal.WriteInt32(entry + 8, index);
        }
        private void Children(nint parent, nint[] children)
        {
            var ptr = Allocate(children.Length * 8);
            Marshal.Copy(children, 0, ptr, children.Length);
            Marshal.WriteIntPtr(parent + Poe2.UiElement.Children, ptr);
            Marshal.WriteIntPtr(parent + Poe2.UiElement.ChildrenEnd, ptr + children.Length * 8);
        }
        public void Dispose() { foreach (var ptr in _allocated) Marshal.FreeHGlobal(ptr); }
    }
}
