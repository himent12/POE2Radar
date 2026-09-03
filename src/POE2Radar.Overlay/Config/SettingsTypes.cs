using System.Text.Json;
using System.Text.Json.Serialization;
namespace POE2Radar.Overlay.Config;

/// <summary>
/// One combat-assist rotation slot: a Win32 virtual-key, a per-skill cooldown, and an optional
/// tighter grid range (0 = use <see cref="RadarSettings.CombatRange"/>).
/// </summary>
public sealed class CombatSkill
{
    public int Key { get; set; } = 0x51; // Q
    public int CooldownMs { get; set; } = 400;
    public float Range { get; set; }
    /// <summary>Only fire when at least this many hostiles are inside the skill's range (AoE gating).</summary>
    public int MinTargets { get; set; } = 1;
    /// <summary>Only fire when the target is rare or unique.</summary>
    public bool RareOnly { get; set; }
    /// <summary>&gt; 0: only fire while the player's life is under this % (defensive / guard skills).</summary>
    public float HpBelowPct { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// A named Atlas colour group (#7): a set of map display names that all draw in one ring/label colour,
/// so a whole category (Citadels, Halls, Uniques, Expedition) recolours together. Adopted from the
/// GameHelper2 Atlas plugin's Map Styles. <see cref="Color"/> is <c>#RRGGBB</c>.
/// </summary>
public sealed class AtlasMapGroup
{
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#e0b341";
    public List<string> Maps { get; set; } = new();
}

/// <summary>
/// A single drawable radar icon: shape, RGB color, opacity, pixel size, and an enable toggle.
/// <see cref="Shape"/> is one of Circle/Triangle/Star/Diamond/Plus/Square (anything else falls back
/// to Circle when rendered); <see cref="Color"/> is <c>#RRGGBB</c>; <see cref="Opacity"/> is 0..1.
/// </summary>
public sealed class IconStyle
{
    public bool Enabled { get; set; } = true;
    public string Shape { get; set; } = "Circle";
    public string Color { get; set; } = "#FFFFFF";
    public float Opacity { get; set; } = 1.0f;
    public float Size { get; set; } = 3.0f;

    public IconStyle() { }
    public IconStyle(string shape, string color, float opacity, float size)
    {
        Shape = shape; Color = color; Opacity = opacity; Size = size;
    }
}

/// <summary>
/// A user-defined "mechanic" highlight: when an entity's metadata contains ANY of <see cref="Match"/>
/// (case-insensitive) AND its category is in <see cref="Categories"/> (if any are listed), it draws
/// this icon instead of its generic category dot — so e.g. an Expedition marker or a Strongbox stands
/// out. The first enabled matching rule wins.
/// </summary>
public sealed class MechanicStyle
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public List<string> Match { get; set; } = new();
    /// <summary>Entity-category gate by <c>Poe2Live.EntityCategory</c> name (e.g. "Monster", "Chest",
    /// "Other"). A rule applies only to these categories; empty = all categories. This stops a broad
    /// match term (e.g. "Expedition") from hijacking the wrong entities — the league POI marker
    /// (category Other) vs. the monsters that spawn during the event (category Monster).</summary>
    public List<string> Categories { get; set; } = new();
    public string Shape { get; set; } = "Star";
    public string Color { get; set; } = "#FFFFFF";
    public float Opacity { get; set; } = 1.0f;
    public float Size { get; set; } = 6.0f;
}

/// <summary>
/// Monster HP-bar geometry. Width, border thickness, and border color are per-rarity; height + X/Y
/// offset are shared. The per-rarity enable flags live on <see cref="RadarSettings"/>
/// (HpBarNormal/Magic/Rare/Unique). The bar *fill* color is taken from the matching monster icon
/// color (so "rare = gold" stays one setting); the border is configured independently below. Border
/// defaults reproduce the old weight-by-rarity cue (Normal undecorated, Magic 1px, Rare/Unique 2px)
/// with borders tinted to match each rarity's icon color.
/// </summary>
public sealed class HpBarSettings
{
    public float Height { get; set; } = 5f;
    public float OffsetX { get; set; } = 0f;
    public float OffsetY { get; set; } = -30f; // px relative to the mob's screen position (neg = up)
    public float WidthNormal { get; set; } = 30f;
    public float WidthMagic { get; set; } = 38f;
    public float WidthRare { get; set; } = 50f;
    public float WidthUnique { get; set; } = 64f;
    // Border thickness in px (0 = no border).
    public float BorderNormal { get; set; } = 0f;
    public float BorderMagic { get; set; } = 1f;
    public float BorderRare { get; set; } = 2f;
    public float BorderUnique { get; set; } = 2f;
    // Border color (#RRGGBB); defaults mirror the per-rarity monster icon colors.
    public string BorderColorNormal { get; set; } = "#FF3333";
    public string BorderColorMagic { get; set; } = "#73A6FF";
    public string BorderColorRare { get; set; } = "#FFD926";
    public string BorderColorUnique { get; set; } = "#FF7300";
}

/// <summary>
/// Walkable-terrain bitmap styling: the interior "wash" over walkable cells and the brighter
/// outline drawn on walkable cells bordering a wall/edge. Color is <c>#RRGGBB</c>; opacity is 0..1
/// (baked into the per-pixel alpha). Defaults reproduce the formerly hardcoded look exactly:
/// interior <c>#506482</c> @ ~30/255, edge <c>#3CDCFF</c> @ ~180/255. The per-area terrain bitmap
/// is rebuilt when any of these change.
/// </summary>
public sealed class TerrainSettings
{
    public string InteriorColor { get; set; } = "#506482";
    public float InteriorOpacity { get; set; } = 0.118f; // → 30/255
    public string EdgeColor { get; set; } = "#3CDCFF";
    public float EdgeOpacity { get; set; } = 0.706f;      // → 180/255
}

/// <summary>
/// Ground-item value overlay: draws a dropped UNIQUE's resolved name + Exalted price over its in-world
/// loot icon (so unidentified uniques reveal what they are), with a border when the value clears
/// <see cref="HighlightMinEx"/>. Prices come from the PriceBook (poe.ninja). <see cref="League"/> blank =
/// auto-detect the current league; set it to override. <see cref="MinQuantity"/> filters low-volume
/// mislistings out of the overlay.
/// </summary>
public sealed class MonolithSettings
{
    public bool Enabled { get; set; } = true;
    // Value tiers (best offered reward, Exalted): green ≥ HighlightMinEx, yellow from 0.6×, neutral below.
    public double HighlightMinEx { get; set; } = 30.0;
    public double MinRewardEx { get; set; } = 1.0;       // hide reward rows below this (panel + dashboard)
    // Whole-structure value gate (Exalted): hide the monolith ENTIRELY — map icon, reward panel/label, AND
    // auto-navigation — when its best offered reward is below this. 0 = show & auto-path every monolith
    // (default; preserves prior behavior). Set e.g. 10 to only surface/auto-route to high-value ones.
    public double MinValueEx { get; set; } = 0.0;
    public bool HideCollected { get; set; } = true;      // hide monoliths whose reward was already claimed
    public bool ShowPanel { get; set; } = true;          // the in-overlay nearby-monolith reward panel
    public bool PanelCollapsed { get; set; } = false;    // panel shrunk to its title bar (click the header to toggle)
    public bool ShowMapLabel { get; set; } = true;       // draw the value + top-reward label at the icon
    public float PanelMaxDistance { get; set; } = 0f;    // 0 = every monolith in the area; else only within N grid
}

/// <summary>
/// Currency Exchange (Kalguur market) order-book overlay: when the exchange panel is open, draws a
/// top-right depth panel listing the best offered + wanted ratios and stock. <see cref="MaxRows"/> caps
/// how many ladder rows each side shows. Mirrors <see cref="GroundItemSettings"/>/<see cref="MonolithSettings"/>.
/// </summary>
public sealed class CurrencyExchangeSettings
{
    public bool Enabled { get; set; } = true;
    public int MaxRows { get; set; } = 8;        // how many ladder rows to draw per side
    public bool Collapsed { get; set; } = false; // card shrunk to a small "expand" tab (click to toggle)
}

public sealed class GroundItemSettings
{
    public bool Enabled { get; set; } = true;
    public double HighlightMinEx { get; set; } = 10.0;   // border/emphasis when value ≥ this many Exalted
    // Per-bucket value FLOORS (Exalted): a drop is labelled only if its value clears the floor for its
    // bucket. Uniques / Currency / everything-else are tuned separately because their value scales differ.
    public double UniqueMinEx { get; set; } = 5.0;       // uniques floor
    public double CurrencyMinEx { get; set; } = 1.0;     // currency floor
    public double OtherMinEx { get; set; } = 1.0;        // floor for everything else (runes/essences/fragments/…)
    // SHARED pricing-confidence threshold (ground overlay + hover): a price backed by a positive-but-lower
    // listing count is FLAGGED with a "?" suffix rather than hidden, so a possible mislisting still shows but
    // reads as uncertain. A reported volume of 0 = "no volume data" (many legit fungibles), never flagged.
    public int MinQuantity { get; set; } = 2;
    public string League { get; set; } = "";             // SHARED price league (ground + hover); blank = auto-detect current league
    // Draw the value chip ON the game's own loot tag (game-computed rect → no projection, no jitter) for
    // everything the game already names (currency/runes/essences/fragments/identified uniques), matched by
    // the tag's text. UNIDENTIFIED uniques (name hidden by the game) always use the world-projected reveal.
    // When false, every priced drop uses the older world-projected chip (the pre-tag behavior).
    public bool AnchorValuesToTags { get; set; } = true;
    // Which item-value category GROUPS get a ground label (see RadarApp.CategoryGroup). Default: uniques +
    // the high-value stackables. Empty list ⇒ nothing shows.
    public List<string> Categories { get; set; } = new()
    {
        "Uniques", "Currency", "Runes", "SoulCores", "Essences", "Fragments",
        "UncutGems", "Delirium", "Tablets", "Idols", "Abyss", "Ritual",
    };
}

/// <summary>Hover price chip: while you hover an item in ANY item UI (inventory / stash / vendor / reward),
/// draw a small price chip beside the game's tooltip. Uniques price by 2D art (works on unidentified ones —
/// the game hides the name); everything else by base name. Stacks show per-unit AND stack total. Unlike the
/// ground overlay this ignores value floors/category toggles — hovering is explicit intent, so any priced
/// item shows.</summary>
public sealed class HoverPriceSettings
{
    public bool Enabled { get; set; } = true;
    public double HighlightMinEx { get; set; } = 10.0;   // emphasize the chip when the (stack) value ≥ this many Exalted
}

/// <summary>
/// The full radar icon style table. Every default mirrors the formerly hardcoded values in
/// <c>OverlayRenderer</c>, so a missing/partial config renders identically to before.
/// </summary>
public sealed class RadarStyles
{
    // Monster dots by rarity.
    public IconStyle MonsterNormal { get; set; } = new("Circle",   "#FF3333", 0.95f, 2.6f);
    public IconStyle MonsterMagic  { get; set; } = new("Fang",     "#73A6FF", 0.97f, 5.5f);
    public IconStyle MonsterRare   { get; set; } = new("Claw",     "#FFD926", 1.00f, 7.5f);
    public IconStyle MonsterUnique { get; set; } = new("Skull",    "#FF7300", 1.00f, 8.0f);

    // Other entity categories.
    public IconStyle Player        { get; set; } = new("Person",  "#4DF2FF", 1.00f, 3.4f);
    public IconStyle Npc           { get; set; } = new("Chat",    "#FFD933", 0.95f, 4.2f);
    public IconStyle ChestRare     { get; set; } = new("Chest",   "#FFD926", 0.95f, 5.0f);
    public IconStyle ChestUnique   { get; set; } = new("Crown",   "#FF7300", 0.95f, 5.5f);
    public IconStyle Transition    { get; set; } = new("Stairs",  "#66FF99", 0.95f, 5.0f);
    public IconStyle Poi           { get; set; } = new("MapPin",  "#8CBFFF", 0.80f, 3.6f);

    // Tile landmarks (shape marker + text label at the group centroid).
    public IconStyle Landmark      { get; set; } = new("Diamond", "#F259F2", 1.00f, 5.0f);

    // Metadata-matched overrides (first enabled match wins). Seeded with common PoE2 mechanics.
    public List<MechanicStyle> Mechanics { get; set; } = new()
    {
        // Match the actual league POI marker ONLY. The old bare "Expedition" substring (with an empty
        // category gate) hijacked EVERY entity carrying "Expedition" in its path — the combat mobs
        // (".../...CrabExpedition", category Monster) and the transient detonation effects
        // (".../Objects/Expedition2EncounterCrack", category Other) all got the marker icon. The
        // dir-qualified key hits only "Expedition2/Expedition2Encounter" (NOT the "/Objects/...Crack"
        // path), and the Other gate keeps it off the monsters. ("ExpeditionEncounter" was also dead —
        // the real path is "Expedition2Encounter" with a digit, so that key matched nothing.)
        new() { Name = "Expedition", Match = new() { "Expedition2/Expedition2Encounter" }, Categories = new() { "Other" }, Shape = "Flag", Color = "#26E6D9", Opacity = 1f, Size = 7f },
        // Ritual/Breach/Essence are gated to the mechanic MARKER (Object/Other) so the bare substring can't
        // hijack the league's combat monsters (e.g. "Metadata/Monsters/LeagueRitual/…", "…LeagueBreach/…").
        new() { Name = "Ritual",     Match = new() { "Ritual" }, Categories = new() { "Object", "Other" },  Shape = "Star",     Color = "#FF3355", Opacity = 1f, Size = 7f },
        new() { Name = "Breach",     Match = new() { "Breach" }, Categories = new() { "Object", "Other" },  Shape = "Portal",   Color = "#A64DFF", Opacity = 1f, Size = 7f },
        // Match the league-strongbox DIRECTORY only ("Metadata/Chests/StrongBoxes/…") and gate to
        // Chest. The bare "Strongbox" term was too broad twice over: it tagged the box's spawned Vaal
        // guards (…Strongbox monsters — now excluded by the Chest gate) AND ordinary area chests that
        // merely carry "Strongbox" in their name (e.g. Chests/KedgeBayChests/KedgeBayChestStrongbox).
        // "StrongBoxes" hits the real boxes (BasicStrongboxLow lives under it) but not those.
        new() { Name = "Strongbox",  Match = new() { "StrongBoxes" }, Categories = new() { "Chest" }, Shape = "Chest", Color = "#FFB300", Opacity = 1f, Size = 6f },
        new() { Name = "Essence",    Match = new() { "Essence" }, Categories = new() { "Object", "Other" }, Shape = "Flask",    Color = "#33E0FF", Opacity = 1f, Size = 7f },
        // Match the real shrine namespace ONLY (Metadata/Shrines/Shrine_Trigger). A bare "Shrine" substring
        // false-positives on terrain cosmetics/spawners (GoblinShrineCosmetic, GoblinShrineSpawnerLeap) and
        // the ShrineFireDaemon effect carrier — none of which are the clickable shrine mechanic.
        new() { Name = "Shrine",     Match = new() { "Metadata/Shrines/" },                  Shape = "Star",     Color = "#7DFF7D", Opacity = 1f, Size = 6f },
    };
}
