// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.Managers
{
    /// <summary>
    /// Every color the item appraisal uses, loaded from
    ///   Data/Client/appraisal_palette.json
    /// and hot-reloaded whenever that file changes on disk.
    ///
    /// Why this exists: tuning a palette by editing C# means rebuild + reinstall +
    /// restart for every single color tweak, which is a terrible loop for something
    /// judged entirely by eye. With the palette external, edits land on the next frame
    /// and the client keeps running. Same auto-reload pattern PickedChestRegistry uses
    /// for its CSVs — mtime checked at most once every RELOAD_INTERVAL_SEC, cheap enough
    /// to call from a render predicate.
    ///
    /// The tracked file in the uo repo is the reference for every knob; the client only
    /// reads it. Anything absent from the file keeps its compiled default.
    ///
    /// Missing or malformed entries fall back to the compiled defaults rather than
    /// throwing — a typo mid-edit should dim one swatch, not crash the client or leave
    /// the UI colorless.
    /// </summary>
    internal static class AppraisalPalette
    {
        private const double RELOAD_INTERVAL_SEC = 2.0;

        private static DateTime _lastCheck = DateTime.MinValue;
        private static DateTime _lastMtime = DateTime.MinValue;
        private static bool _loaded;

        private static string PalettePath =>
            Path.Combine(CUOEnviroment.ExecutablePath, "Data", "Client", "appraisal_palette.json");

        // ── Compiled defaults ────────────────────────────────────────────────────────
        // Tier is ordinal and owns the outer border. Because wand type now lives on its
        // own element (the left bar), tiers no longer have to share hue space with the
        // 13 wand colors — so they can go back to five genuinely distinct, vivid hues
        // in the loot-rarity order players already know.
        // Destiny / Destiny 2 rarity ramp: Basic / Uncommon / Rare / Legendary / Exotic.
        //
        // Destiny's shipped values are, in order:
        //   #C3BCB4  #366F42  #5076A3  #522F65  #CEAE33
        // Those are designed as BACKGROUND FILLS behind an item icon, where a deep,
        // desaturated tone reads as premium. As a 1px outline on a dark slot they fail:
        // Uncommon lands at luminance 96 and Legendary at 58, giving only 74 and 71
        // contrast against the backdrop — effectively invisible.
        //
        // So HUE and SATURATION are Destiny's exactly; only VALUE is lifted, and only on
        // the two that needed it, until each clears a luminance floor of ~128. Basic and
        // Exotic are already bright enough and are used verbatim. The result keeps
        // Destiny's colour identity while surviving the change of medium.
        //
        // To use the shipped values instead, paste the row above into appraisal_palette.json
        // — it hot-reloads, no restart.
        private static readonly Color[] DefaultTiers =
        {
            new Color(0xC3, 0xBC, 0xB4),   // Basic     — Destiny verbatim
            new Color(0x49, 0x95, 0x59),   // Uncommon  — hue/sat kept, value 0.44 -> 0.59
            new Color(0x5B, 0x87, 0xBA),   // Rare      — hue/sat kept, value 0.64 -> 0.73
            new Color(0xB5, 0x68, 0xDF),   // Legendary — hue/sat kept, value 0.40 -> 0.88
            // Exotic lifted off Destiny's verbatim #CEAE33, which sits at hue 47.6 —
            // right on the orange/gold boundary — and whose low value (0.81) let it read
            // as muddy orange. Now hue 52.3, value 0.94: clearly yellow-gold.
            // Saturation is deliberately not pushed further; past ~hue 55 it starts
            // reading as lemon rather than gold, and it closes on the bone-white T1
            // (110 apart here, 100 by hue 55).
            new Color(0xF0, 0xDC, 0x55),   // Exotic    — yellow gold
        };

        private static readonly Dictionary<string, Color> DefaultSlayers = new()
        {
            ["Undead"] = new Color(0xB0, 0xD8, 0xFF),
            ["Repond"] = new Color(0xE8, 0x5C, 0xA8),
            ["Reptilian"] = new Color(0x4F, 0xC4, 0x6A),
            ["Demonic"] = new Color(0xF0, 0x50, 0x3C),
            ["Arachnid"] = new Color(0x9B, 0x4F, 0xE8),
            ["Elemental"] = new Color(0x2E, 0xD0, 0xC4),
        };

        // Keyed by the charge-type token (without the trailing " charges").
        private static readonly Dictionary<string, Color> DefaultWands = new()
        {
            ["identification"] = new Color(0x18, 0xF2, 0xF2),
            ["greater healing"] = new Color(0x2A, 0xE8, 0x5A),
            ["healing"] = new Color(0x8C, 0xF0, 0xB0),
            ["mana drain"] = new Color(0x7B, 0x4D, 0xF2),
            ["lightning"] = new Color(0x2E, 0x7B, 0xFF),
            ["fireball"] = new Color(0xFF, 0x7A, 0x1A),
            ["harm"] = new Color(0x8C, 0x14, 0x20),
            ["magic arrow"] = new Color(0xFF, 0xE0, 0x3A),
            ["poison"] = new Color(0x86, 0xE0, 0x1E),
            ["lesser poison"] = new Color(0xB4, 0xE8, 0x6A),
            ["greater poison"] = new Color(0x4E, 0x94, 0x10),
            ["deadly poison"] = new Color(0x2E, 0x64, 0x08),
            ["clumsiness"] = new Color(0xC0, 0x60, 0xD0),
            ["feeblemind"] = new Color(0x4F, 0xA8, 0x9E),
            ["weakness"] = new Color(0x7A, 0x6E, 0x96),
            ["curse"] = new Color(0x9B, 0x4F, 0xC4),
            ["teleport"] = new Color(0x40, 0xC4, 0xFF),
            ["invisibility"] = new Color(0xB3, 0x9D, 0xFF),
            ["night sight"] = new Color(0x8F, 0xD8, 0xFF),
            ["spell reflection"] = new Color(0xFF, 0x57, 0xD9),
            ["curing"] = new Color(0x2F, 0xBF, 0xA0),
            ["restoration"] = new Color(0xB7, 0xF5, 0xC9),
            ["paralyzation"] = new Color(0x5C, 0x6B, 0xFF),
            ["bless"] = new Color(0xFF, 0xF0, 0xC0),
            ["strength"] = new Color(0xE0, 0xA0, 0x30),
            ["agility"] = new Color(0xD0, 0xC0, 0x40),
            ["cunning"] = new Color(0xC0, 0xB0, 0x60),
            ["protection"] = new Color(0x7F, 0xA8, 0xD0),
        };

        // Neutral mid grey. It had to move off white once T1 took that slot — an
        // unidentified item and a common one are very different things and must not
        // share a color. Grey reads as "unknown" and, being the only fully desaturated
        // entry besides bone white, is separated from it by luminance (138 vs 220).
        private static readonly Color DefaultUnidentified = new Color(0x90, 0x90, 0x90);
        private static readonly Color DefaultDurabilityPip = new Color(0x96, 0xA5, 0xB4);
        private static readonly Color DefaultGenericWand = new Color(0x9C, 0x7B, 0xE0);

        // Neutral edge for the universal container outline. Only the HUE is taken from
        // here — opacity is computed per container from how full it is, so this wants to
        // be something quiet that reads at low alpha against both grass and stone.
        private static readonly Color DefaultContainerOutline = new Color(0x8E, 0xA6, 0xC0);

        // Capacity ramp: colour of a container's FILLED portion, keyed to its weight on
        // a log scale up to 100000 stones. Cool green (light) through amber to deep red
        // (very heavy) — the standard "load" reading, and deliberately not any of the
        // rarity hues, since this is a different kind of claim about a different object.
        private static readonly Color[] DefaultCapacityRamp =
        {
            new Color(0x6E, 0xC8, 0x8A),   // light
            new Color(0xC8, 0xD4, 0x64),
            new Color(0xE8, 0xB4, 0x4A),
            new Color(0xE0, 0x7A, 0x38),
            new Color(0xC8, 0x3A, 0x38),   // very heavy
        };

        // Extra border thickness granted to the top tier (Vanquishing / Invulnerability),
        // so it carries visual weight and not only a colour. Exposed in the JSON because
        // it is a taste call that wants trying at 0, 1 and 2 without a rebuild each time.
        private const int DefaultTopTierBorderBonus = 1;

        // Shard-custom rares that REUSE ordinary art, so the graphic cannot identify them
        // and only the name can. Marijuana is drawn with the Nightshade graphic (0x0F88),
        // shared with 144 ordinary reagents in this player's own data; a Water Bong uses
        // the generic bottle art. Matched as a case-insensitive substring of the OPL name.
        private static readonly string[] DefaultRareNames =
        {
            "water bong",
            "handrolled joint",
            "marijuana",
            "crystal ball",
        };

        // Rares are identified by GRAPHIC, not by text: nothing in a rare's tooltip says
        // "rare" — that status is player/collector knowledge, so it has to be a list.
        //
        // WHOLE FAMILIES are listed, not just the graphic seen in the player's chest.
        // Lighting a candle or candelabra swaps its graphic to a lit variant (and lit
        // items have several animation frames), so a single-graphic entry silently stops
        // matching the moment the item is switched on — which is exactly how the first
        // version lost track of them. Families were read out of tiledata.mul by name, so
        // every state is covered without having to know the on/off pairings.
        //
        // Kept in the JSON so the list can grow as more are found, without a rebuild.
        private static readonly ushort[] DefaultRareGraphics =
        {
            // Only graphics CONFIRMED in the player's own rares chest / table, plus the
            // lit frames immediately adjacent to each. An earlier revision added whole
            // name-matched families out of tiledata, which swept in ordinary house decor
            // — the common candle and the short table candelabra among them.
            0x0A29,                                   // candelabra (tall floor)
            // Candle 0x0A26 pairs with the 0x0B1A lit frames, NOT the 0x0A0F group:
            // unlit 11x26 -> lit 11x30 (same width, 4px taller for the flame), whereas
            // 0x0A0F is 7x18 and pairs with the common 0x0A28 candle at 7x15.
            0x0A26, 0x0B1A, 0x0B1B, 0x0B1C,           // candle (tall single) + lit frames
            0x142C, 0x142D, 0x142E, 0x142F,           // candle + lit frames
            0x1430, 0x1431, 0x1432, 0x1433,           // candle (tall) + lit frames
            0x1857, 0x1858, 0x1859, 0x185A,           // skull with candle + lit frames
            // PLURAL "bottles of liquor" only. The singular 0x099B "a bottle of liquor"
            // is common tavern decor — ~45 of them exist across the world here — so it is
            // deliberately excluded even though it sits on the same table.
            0x099C, 0x099D, 0x099E,                   // bottles of liquor (plural)
            0x0E44, 0x0E45, 0x0E46, 0x0E47,           // empty jars
            0x0E48, 0x0E49, 0x0E4A, 0x0E4B,           // full jars
            0x0E4C, 0x0E4D, 0x0E4E, 0x0E4F,           // jars
            0x1005, 0x1006, 0x1007,                   // empty / full / half-empty jar
            0x0C41, 0x0C42,                           // dried herbs
            0x0C3B, 0x0C3C, 0x0C3D, 0x0C3E,           // dried flowers
            0x166E, 0x166F,                           // whip
            // PLURAL "gold ingots" only. The singular arts (0x1BE9, 0x1BEC) are left out
            // on the same principle as the liquor bottles. Note gold ingots are also a
            // normal crafting resource, so a mined stack would match these too.
            0x1BEA, 0x1BEB, 0x1BED, 0x1BEE,           // gold ingots
            0x0E2D, 0x0E2E, 0x0E2F, 0x0E30,           // crystal ball
            // Shard-custom items on borrowed art, but with NO ordinary counterpart in
            // this player's data, so the graphic is safe and — unlike a name match —
            // works before the item's tooltip has ever been fetched.
            0x1420, 0x1421,                           // a handrolled joint ("roll of string" art)
            0x0E28,                                   // a water bong ("bottle" art)
        };

        // Potion kegs, keyed by potion family. UO's own potion colours — the same ones
        // the shard uses for an unnamed keg ("A keg of yellow liquid" is heal) — lifted
        // to read as a border on a dark grid slot. Night sight's black cannot, so it
        // takes a slate blue. "unknown" covers a keg whose contents can't be named.
        private static readonly Dictionary<string, Color> DefaultKegs = new()
        {
            ["heal"] = new Color(0xF2, 0xD4, 0x3A),
            ["cure"] = new Color(0xFF, 0x8C, 0x1A),
            ["poison"] = new Color(0x4F, 0xD0, 0x3A),
            ["agility"] = new Color(0x3A, 0x8C, 0xFF),
            ["strength"] = new Color(0xF0, 0xF0, 0xF0),
            ["refresh"] = new Color(0xE8, 0x38, 0x3A),
            ["explosion"] = new Color(0xB2, 0x5C, 0xFF),
            ["nightsight"] = new Color(0x7A, 0x86, 0xA8),
            ["unknown"] = new Color(0xB4, 0xA0, 0x82),
        };


        // ── Highlight grades ─────────────────────────────────────────────────────────
        // How LOUD a collectible is, separate from what colour it is. A seed keeps its
        // own colour and a magic hat its bonus colour; the grade decides weight, alpha,
        // pulse, label, chime and how far it shows through walls. Four steps because
        // the crate next to Walter holds all four: a common Magical Wizard's Hat that
        // should barely register, ordinary decor rares, magic clothing and jewelry, and
        // the callouts (a common seed that grows a bonsai).
        private static readonly Dictionary<HighlightGrade, GradeStyle> DefaultGrades = new()
        {
            [HighlightGrade.Trace] = new GradeStyle(new Color(0xC9, 0xA2, 0xB4), 0.4f, 1, false, 946, 0, 0f, false),
            [HighlightGrade.Rare] = new GradeStyle(new Color(0xFF, 0x6F, 0xA8), 1f, 1, false, 1166, 0, 0.45f, false),
            [HighlightGrade.Prized] = new GradeStyle(new Color(0xFF, 0x5C, 0xC8), 1f, 2, true, 1166, 0x055, 0.8f, false),
            [HighlightGrade.Callout] = new GradeStyle(new Color(0xFF, 0xE0, 0x7A), 1f, 2, true, 53, 0x1EB, 1f, true),
        };

        // Exceptional is quality, not power, so it gets a colour outside the tier ramp:
        // copper. Before this it borrowed tier 1's bone white on single-channel
        // surfaces, which made an exceptional Ruin weapon and a plain exceptional one
        // look identical.
        private static readonly Color DefaultExceptional = new Color(0xD9, 0x89, 0x4A);

        // Pulse: alpha swings between floor and 1 over period seconds, eased by a sine.
        private const float DefaultPulsePeriod = 1.8f;
        private const float DefaultPulseFloor = 0.35f;

        // X-ray opacity for container outlines, by state; appraisal x-ray rides on the
        // grade. Culled = above the player's ceiling, drawn without a depth test, so
        // the range keeps a house full of upstairs rares from flooding the screen.
        private const float DefaultXrayChest = 0.65f;
        private const float DefaultXrayLooted = 0.25f;
        private const float DefaultXrayCapacity = 0f;
        private const int DefaultXrayCulledRange = 24;

        // Minimum seconds between two first-sight chimes; the journal line still prints.
        private const float DefaultChimeGap = 2f;

        // ServUO PlantHue, in the order a plant breeds them. Tier is colour rarity:
        // 1 the four colours seeds drop in, 2 bright primaries and the secondaries,
        // 3 bright secondaries, 4 the mutants. The outline colour is read from the
        // hue itself at draw time, so a seed is outlined in its own colour.
        private static readonly (string Name, ushort Hue, int Tier)[] DefaultSeedColors =
        {
            ("plain", 0, 1), ("red", 1645, 1), ("blue", 1341, 1), ("yellow", 2213, 1),
            ("bright red", 33, 2), ("bright blue", 5, 2), ("bright yellow", 56, 2),
            ("purple", 13, 2), ("green", 1435, 2), ("orange", 1135, 2),
            ("bright purple", 16, 3), ("bright green", 66, 3), ("bright orange", 43, 3),
            ("black", 1109, 4), ("white", 1153, 4), ("pink", 1166, 4),
            ("magenta", 1158, 4), ("aqua", 1173, 4), ("fire red", 1161, 4),
        };

        // A plain seed is unhued; this stands in for a colour it doesn't have.
        private static readonly Color DefaultPlainSeed = new Color(0xD8, 0xC8, 0xA0);

        // ServUO PlantType order; a seed's index is its position here.
        private static readonly string[] DefaultPlantTypes =
        {
            "campion flowers", "poppies", "snowdrops", "bulrushes", "lilies",
            "pampas grass", "rushes", "elephant ear plant", "fern", "ponytail palm",
            "small palm", "century plant", "water plant", "snake plant",
            "prickly pear cactus", "barrel cactus", "tribarrel cactus",
        };

        // Seeds named for a category rather than a colour grow bonsai ("common seed").
        private static readonly string[] DefaultBonsaiCategories =
        {
            "common", "uncommon", "rare", "exceptional", "exotic", "peculiar", "fragrant",
        };

        // Bonus colours: stats in the colours UO players already read them in, skills
        // in gold. Hues for the text labels, colours for outlines.
        private static readonly Dictionary<string, Color> DefaultBonusColors = new()
        {
            ["str"] = new Color(0xF0, 0x5A, 0x4A),
            ["dex"] = new Color(0x5C, 0xD6, 0x7C),
            ["int"] = new Color(0x5E, 0x9E, 0xF5),
            ["skill"] = new Color(0xF2, 0xC8, 0x50),
        };

        private static readonly Dictionary<string, ushort> DefaultBonusHues = new()
        {
            ["str"] = 33,
            ["dex"] = 68,
            ["int"] = 88,
            ["skill"] = 2213,
        };

        // Spell scrolls, 0x1F2D-0x1F6C, first circle to eighth. Six of them are named
        // "Magic Arrow", "Magic Trap", "Magic Untrap", "Magic Lock", "Magic Unlock" and
        // "Magic Reflection", which the "^magic " clothing rule below took for magic
        // clothing and drew in Prized pink.
        private static readonly HashSet<ushort> SpellScrollGraphics = Range(0x1F2D, 0x1F6C);

        // From tiledata.mul: bear mask 0x1545/0x1546, deer mask 0x1547/0x1548, and the
        // bone arms, armor, gloves, helmet and legs, 0x144E-0x1457.
        private static readonly HashSet<ushort> BearMaskGraphics = new() { 0x1545, 0x1546 };
        private static readonly HashSet<ushort> DeerMaskGraphics = new() { 0x1547, 0x1548 };
        private static readonly HashSet<ushort> BoneArmorGraphics = Range(0x144E, 0x1457);

        // Coloured leather, which a tailor's bone armor takes the hue of: the shard's
        // spined 0x08AC, horned 0x0845 and barbed 0x0851 (_ui.METAL_HUE, from its BODs),
        // and later RunUO's 0x0283, 0x0227 and 0x01C1.
        private static readonly HueSet LeatherHues = new(0x08AC, 0x0845, 0x0851, 0x0283, 0x0227, 0x01C1);

        // Collector rules, first match wins, ahead of every other grade source. A rule
        // graded None is a deliberate "not a collectible" and stops every other source.
        private static readonly RareRule[] DefaultRareRules =
        {
            // A shard-named tree Walter stood beside (journal 2026-11-13, "You see: a
            // silver tree"), most likely the source of silverwood, the last of the nine
            // woods and probably a 100 Lumberjacking cut. It is set dressing to the
            // server (not movable), so only a landmark rule reaches it, and only once
            // its name has come in: its art and hue are not known yet.
            new RareRule("silver tree", null, true, HighlightGrade.Callout, "\u2605 Silver Tree", landmark: true),
            new RareRule(null, SpellScrollGraphics, null, HighlightGrade.None, null),
            // Ordinary magic clothing: the shard renamed it only because "magical
            // wizard's hat" was taken by the common vendor hat below.
            new RareRule("really really magic wizards hat", null, null, HighlightGrade.Prized, null),
            new RareRule("a magical wizard's hat", null, null, HighlightGrade.Trace, null),
            // Deer and bear hats, "rare spawns on ... deer and bears" (2009 page): night
            // sight and +5 Animal Lore. The white polar bear hat and white stag mask give
            // +10; ordinary masks are unhued, so a hued one is the white. Ahead of the
            // magic rules, so a magic one still reads as the hat it is.
            new RareRule(null, BearMaskGraphics, null, HighlightGrade.Callout, "\u2605 Polar Bear Hat", hued: true),
            new RareRule(null, DeerMaskGraphics, null, HighlightGrade.Callout, "\u2605 White Stag Mask", hued: true),
            new RareRule(null, BearMaskGraphics, null, HighlightGrade.Prized, "Bear Hat"),
            new RareRule(null, DeerMaskGraphics, null, HighlightGrade.Prized, "Deer Hat"),
            // Mage bone armor, "very rare ... recognized by its dark color": bone armor
            // with no meditation penalty and a little INT. Ordinary bone armor is unhued
            // (two chest finds, both 0) unless a tailor made it from coloured leather,
            // so any hue but the leathers' is the mage's. By name too, in case the
            // tooltip says so.
            new RareRule("mage bone", BoneArmorGraphics, null, HighlightGrade.Callout, "\u2605 Mage Bone Armor"),
            new RareRule(null, BoneArmorGraphics, null, HighlightGrade.Callout, "\u2605 Mage Bone Armor",
                hued: true, exceptHues: LeatherHues),
            new RareRule("antique", null, null, HighlightGrade.Prized, null),
            // Magic clothing is prized before its tooltip arrives; with the tooltip, the
            // Blessed + bonus lines say the same thing.
            new RareRule("^magic ", null, null, HighlightGrade.Prized, null),
            // Trays dress tables all over town (57 on the ground in this player's data),
            // so only one that has been picked up counts.
            new RareRule(null, new HashSet<ushort> { 0x0991, 0x0992 }, false, HighlightGrade.Rare, null),
            // Fertile Dirt, wanted for every plant. By graphic, so it shows before its
            // tooltip arrives (seed_harvester confirmed 0x0F81 in harvest drops).
            new RareRule(null, new HashSet<ushort> { 0x0F81 }, null, HighlightGrade.Rare, null),
            // The black sheep's wool, the only black dye on the shard (2009 server
            // page). Ordinary wool is unhued, so any hue on wool art is the black.
            new RareRule(null, new HashSet<ushort> { 0x0DF8, 0x0DF9 }, null, HighlightGrade.Prized, "Black Wool", hued: true),
        };

        // Creatures worth a callout, first match wins. The 2009 server page: rare spawns
        // of many species, 1 in 200 to 1 in 1000, mixed in with their kind, and the
        // Moonglow Zoo shows them off. The zoo's census (2026-11-11, creature_census.py)
        // found every rare in a colour its species never wears otherwise, and under a name
        // of its own ("a black wolf", "an ancient gorilla", "a silver drake"), so a rule is
        // a body plus the colours that are not ordinary, labelled by the creature's name.
        //
        // Callout: the page's "rare colors and increased stats" species. Prized: its
        // "rare colors" only, and the zoo's rare bears and harts, which it does not list.
        private static readonly HashSet<ushort> BirdBodies = new() { 0x06 };

        private static readonly MobileRule[] DefaultRareMobiles =
        {
            // The black sheep ("a very valuable colored black wool"): nobody has seen one,
            // so by name, and as any hued sheep body (0xCF woolly, 0xDF shorn).
            new MobileRule("black sheep", null, null, HighlightGrade.Callout, "\u2605 Black Sheep"),
            new MobileRule(null, new HashSet<ushort> { 0xCF, 0xDF }, true, HighlightGrade.Callout, "\u2605 Black Sheep"),

            // Rare colours, increased stats. Every one of these bodies is unhued in the
            // wild, so any hue is the rare. Zoo: black wolf 0x17/0x076B, arctic wolf
            // 0x25/0x0481, giant black scorpion 0x30/0x076B, a black giant spider
            // 0x1C/0x076B, war horse 0xC8/0x076B, ancient gorilla 0x1D/0x0482, silver drake
            // 0x3D/0x076D, green drake 0x3D/0x08A4. The page's drake and dragon colours
            // slay: gold elementals, silver undead, blue humanoids, green snakes, black dragons.
            new MobileRule(null, new HashSet<ushort> { 0x17 }, true, HighlightGrade.Callout, null),               // dire wolf
            new MobileRule(null, new HashSet<ushort> { 0x22, 0x25 }, true, HighlightGrade.Callout, null),         // white wolf
            new MobileRule(null, new HashSet<ushort> { 0x30 }, true, HighlightGrade.Callout, null),               // scorpion
            new MobileRule(null, new HashSet<ushort> { 0x1C, 0x14, 0x0B }, true, HighlightGrade.Callout, null),   // giant, frost, dread spider
            new MobileRule(null, new HashSet<ushort> { 0xC8, 0xCC, 0xE2, 0xE4 }, true, HighlightGrade.Callout, null), // horses
            new MobileRule(null, new HashSet<ushort> { 0x1D }, true, HighlightGrade.Callout, null),               // gorilla
            new MobileRule(null, new HashSet<ushort> { 0x3C, 0x3D }, true, HighlightGrade.Callout, null),         // drakes
            new MobileRule(null, new HashSet<ushort> { 0x0C, 0x3B }, true, HighlightGrade.Callout, null),         // dragons
            new MobileRule(null, new HashSet<ushort> { 0xB4 }, true, HighlightGrade.Callout, null),               // white wyrm

            // Rare colours only. Zoo: albino giant rat 0xD7/0x047E, white hare 0xCD/0x047E,
            // white stag 0xEA/0x047E, dire bear 0xD4/0x0452. Rabbits are hued in the wild,
            // so a hare is matched by its white or its name; rats, dolphins, bears and
            // harts are not.
            new MobileRule(null, new HashSet<ushort> { 0xD7, 0xEE }, true, HighlightGrade.Prized, null),          // giant, sewer rat
            new MobileRule(null, new HashSet<ushort> { 0x97 }, true, HighlightGrade.Prized, null),                // dolphin
            new MobileRule(null, new HashSet<ushort> { 0xD4 }, true, HighlightGrade.Prized, null),                // grizzly bear
            new MobileRule(null, new HashSet<ushort> { 0xEA }, true, HighlightGrade.Prized, null),                // great hart
            new MobileRule("white hare", null, null, HighlightGrade.Prized, null),
            new MobileRule(null, new HashSet<ushort> { 0xCD }, null, HighlightGrade.Prized, null, new HueSet(0x047E)), // rabbit

            // Birds share one body. Ravens, crows and magpies wear 0x0901, every other wild
            // bird a hue in 0x0835-0x0853 (the zoo's commons: 0x0836-0x0851), and so does
            // an ordinary tropical bird. The zoo's rare tropical birds wore 0x0556, 0x055C,
            // 0x055F and 0x0562, so a bird in any other colour is the rare.
            new MobileRule(null, BirdBodies, true, HighlightGrade.Prized, null,
                exceptHues: new HueSet(0x0901).Range(0x0835, 0x0853)),
        };

        // Loose items: on the ground, movable, and in a town (a guard zone) or inside a
        // house. Not a grade, so no label and no chime; only a thin outline in a colour
        // no grade or tier uses. A house's loose items show through walls, floors and
        // ceilings, so what a burglar can carry off is plain from the street; a town's
        // stay in plain sight only, or every shop's shelves would flood the screen.
        private static readonly LooseStyle DefaultLooseTown = new LooseStyle(new Color(0x5C, 0xE0, 0xFF), 0.75f, 0f);
        private static readonly LooseStyle DefaultLooseHouse = new LooseStyle(new Color(0xA6, 0xFF, 0x4D), 0.9f, 0.7f);
        private const string DefaultLooseZones = "in_mani_ylem_guard_zones.zones.json";

        private static HashSet<ushort> Range(ushort first, ushort last)
        {
            var set = new HashSet<ushort>();
            for (int g = first; g <= last; g++) set.Add((ushort)g);
            return set;
        }

        // Containers whose whole contents count as rare: the large crate next to
        // Walter Goonsman. Containers inside are holders, not items, and are skipped.
        private static readonly Dictionary<uint, HighlightGrade> DefaultRareContainers = new()
        {
            [1075862537] = HighlightGrade.Rare,
        };

        // ── Live values ──────────────────────────────────────────────────────────────
        private static Color[] _tiers = (Color[])DefaultTiers.Clone();
        private static Dictionary<string, Color> _slayers = new(DefaultSlayers);
        private static Dictionary<string, Color> _wands = new(DefaultWands, StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, Color> _kegs = new(DefaultKegs, StringComparer.OrdinalIgnoreCase);
        private static Color _unidentified = DefaultUnidentified;
        private static Color _durabilityPip = DefaultDurabilityPip;
        private static Color _genericWand = DefaultGenericWand;
        private static Color _containerOutline = DefaultContainerOutline;
        private static Color[] _capacityRamp = (Color[])DefaultCapacityRamp.Clone();
        private static string[] _rareNames = (string[])DefaultRareNames.Clone();
        private static int _topTierBorderBonus = DefaultTopTierBorderBonus;
        private static HashSet<ushort> _rareGraphics = new(DefaultRareGraphics);
        private static Dictionary<HighlightGrade, GradeStyle> _grades = new(DefaultGrades);
        private static Color _exceptional = DefaultExceptional;
        private static float _pulsePeriod = DefaultPulsePeriod;
        private static float _pulseFloor = DefaultPulseFloor;
        private static float _xrayChest = DefaultXrayChest;
        private static float _xrayLooted = DefaultXrayLooted;
        private static float _xrayCapacity = DefaultXrayCapacity;
        private static int _xrayCulledRange = DefaultXrayCulledRange;
        private static float _chimeGap = DefaultChimeGap;
        private static (string Name, ushort Hue, int Tier)[] _seedColors = DefaultSeedColors;
        private static (string Name, ushort Hue, int Tier)[] _seedColorsByLength = ByLength(DefaultSeedColors);
        private static Color _plainSeed = DefaultPlainSeed;
        private static Dictionary<string, Color> _seedOverride = new(StringComparer.OrdinalIgnoreCase);
        private static string[] _plantTypes = DefaultPlantTypes;
        private static string[] _bonsaiCategories = DefaultBonsaiCategories;
        private static Dictionary<string, Color> _bonusColors = new(DefaultBonusColors, StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, ushort> _bonusHues = new(DefaultBonusHues, StringComparer.OrdinalIgnoreCase);
        private static RareRule[] _rareRules = DefaultRareRules;
        private static MobileRule[] _rareMobiles = DefaultRareMobiles;
        private static LooseStyle _looseTown = DefaultLooseTown;
        private static LooseStyle _looseHouse = DefaultLooseHouse;
        private static string _looseZones = DefaultLooseZones;
        private static Dictionary<uint, HighlightGrade> _rareContainers = new(DefaultRareContainers);

        public static Color Tier(int tier)
        {
            MaybeReload();
            if (tier < 1) tier = 1;
            if (tier > _tiers.Length) tier = _tiers.Length;
            return _tiers[tier - 1];
        }

        public static Color Unidentified { get { MaybeReload(); return _unidentified; } }
        public static Color DurabilityPip { get { MaybeReload(); return _durabilityPip; } }
        public static Color GenericWand { get { MaybeReload(); return _genericWand; } }
        public static Color ContainerOutline { get { MaybeReload(); return _containerOutline; } }

        /// <summary>Capacity-ramp colour for t in 0..1, linearly interpolated between stops.</summary>
        public static Color CapacityRamp(float t)
        {
            MaybeReload();
            Color[] r = _capacityRamp;
            if (r.Length == 0) return Color.White;
            if (t <= 0f) return r[0];
            if (t >= 1f) return r[r.Length - 1];
            float pos = t * (r.Length - 1);
            int i = (int)pos;
            float f = pos - i;
            Color a = r[i], b = r[Math.Min(i + 1, r.Length - 1)];
            return new Color(
                (byte)(a.R + (b.R - a.R) * f),
                (byte)(a.G + (b.G - a.G) * f),
                (byte)(a.B + (b.B - a.B) * f));
        }
        public static int TopTierBorderBonus { get { MaybeReload(); return _topTierBorderBonus; } }
        public static Color RareOutline => Grade(HighlightGrade.Rare).Color;

        public static GradeStyle Grade(HighlightGrade g)
        {
            MaybeReload();
            return _grades.TryGetValue(g, out GradeStyle st) ? st : DefaultGrades[HighlightGrade.Rare];
        }

        public static Color Exceptional { get { MaybeReload(); return _exceptional; } }

        /// <summary>Alpha multiplier for a pulsing highlight at time <paramref name="ms"/>.</summary>
        public static float Pulse(uint ms)
        {
            MaybeReload();
            float period = Math.Max(0.2f, _pulsePeriod) * 1000f;
            float phase = (ms % (uint)period) / period;
            float wave = 0.5f + 0.5f * (float)Math.Sin(phase * Math.PI * 2);
            return _pulseFloor + (1f - _pulseFloor) * wave;
        }

        public static float XrayChest { get { MaybeReload(); return _xrayChest; } }
        public static float XrayLooted { get { MaybeReload(); return _xrayLooted; } }
        public static float XrayCapacity { get { MaybeReload(); return _xrayCapacity; } }
        public static int XrayCulledRange { get { MaybeReload(); return _xrayCulledRange; } }
        public static float ChimeGap { get { MaybeReload(); return _chimeGap; } }
        public static Color PlainSeed { get { MaybeReload(); return _plainSeed; } }

        /// <summary>
        /// Outline colour for a seed: the palette's override, else the seed's own hue
        /// read from hues.mul, else the plain stand-in. Never the raw hue table.
        /// </summary>
        public static Color SeedOutline(string colorName, ushort hue)
        {
            MaybeReload();
            if (colorName != null && _seedOverride.TryGetValue(colorName, out Color c)) return c;
            return hue == 0 ? _plainSeed : HueColor(hue) ?? _plainSeed;
        }

        // ── Hue colours ──────────────────────────────────────────────────────────────
        // A hue is a 32-shade ramp; an outline needs one colour. Shade 24 is where a hue
        // reads as itself on cloth, and a luminance floor keeps black seeds and dark
        // dyes from drawing an outline the ground swallows. Cached per hue, since the
        // hue file never changes under a running client.
        private const int HueShade = 24;
        private const float HueLumaFloor = 110f;
        private static readonly Dictionary<ushort, Color?> _hueColors = new();

        public static Color? HueColor(ushort hue)
        {
            if (hue == 0) return null;
            if (_hueColors.TryGetValue(hue, out Color? cached)) return cached;

            Color? result = null;
            try
            {
                var hues = Client.Game.UO.FileManager.Hues;
                int h = (hue & 0x3FFF) - 1;
                if (h >= 0 && h < hues.HuesCount)
                {
                    uint packed = HuesHelper.Color16To32(hues.HuesRange[h >> 3].Entries[h % 8].ColorTable[HueShade]);
                    var c = new Color { PackedValue = packed | 0xFF000000u };
                    float luma = 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;
                    if (luma < HueLumaFloor)
                    {
                        // Lift toward white, keeping the hue's direction.
                        float t = (HueLumaFloor - luma) / (255f - Math.Max(luma, 1f));
                        c = Color.Lerp(c, Color.White, Math.Clamp(t, 0f, 1f));
                    }
                    result = c;
                }
            }
            catch (Exception)
            {
                // No hue data (unit tests, or before the files load): no colour.
                return null;
            }
            _hueColors[hue] = result;
            return result;
        }

        /// <summary>Seed colours longest name first, so "bright red" wins over "red".</summary>
        public static (string Name, ushort Hue, int Tier)[] SeedColorsByLength()
        {
            MaybeReload();
            return _seedColorsByLength;
        }

        public static bool TrySeedColor(string name, out ushort hue, out int tier)
        {
            MaybeReload();
            foreach ((string n, ushort h, int t) in _seedColors)
            {
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                {
                    hue = h;
                    tier = t;
                    return true;
                }
            }
            hue = 0;
            tier = 0;
            return false;
        }

        /// <summary>Colour name for a seed's hue, when its name doesn't carry one.</summary>
        public static string SeedColorForHue(ushort hue)
        {
            MaybeReload();
            foreach ((string n, ushort h, int _) in _seedColors)
                if (h == hue) return n;
            return null;
        }

        public static string[] PlantTypes { get { MaybeReload(); return _plantTypes; } }
        public static string[] BonsaiCategories { get { MaybeReload(); return _bonsaiCategories; } }

        public static Color BonusColor(string key)
        {
            MaybeReload();
            return key != null && _bonusColors.TryGetValue(key, out Color c) ? c : DefaultBonusColors["skill"];
        }

        public static ushort BonusHue(string key)
        {
            MaybeReload();
            return key != null && _bonusHues.TryGetValue(key, out ushort h) ? h : DefaultBonusHues["skill"];
        }

        /// <summary>First collector rule matching the item, or false.</summary>
        public static bool MatchRule(string name, ushort graphic, bool onGround, ushort hue, out HighlightGrade grade, out string label,
            bool fixedItem = false)
        {
            MaybeReload();
            foreach (RareRule rule in _rareRules)
            {
                if ((!fixedItem || rule.Landmark) && rule.Matches(name, graphic, onGround, hue))
                {
                    grade = rule.Grade;
                    label = rule.Label;
                    return true;
                }
            }
            grade = HighlightGrade.None;
            label = null;
            return false;
        }

        /// <summary>First creature rule matching the mobile, or false.</summary>
        public static bool MatchMobile(string name, ushort body, ushort hue, out HighlightGrade grade, out string label)
        {
            MaybeReload();
            foreach (MobileRule rule in _rareMobiles)
            {
                if (rule.Matches(name, body, hue))
                {
                    grade = rule.Grade;
                    label = rule.Label ?? CreatureLabel(name);
                    return true;
                }
            }
            grade = HighlightGrade.None;
            label = null;
            return false;
        }

        /// <summary>"a black wolf" → "★ Black Wolf"; the shard names each rare colour.</summary>
        internal static string CreatureLabel(string name)
        {
            string n = (name ?? "").Trim();
            foreach (string article in new[] { "a ", "an ", "the " })
                if (n.StartsWith(article, StringComparison.OrdinalIgnoreCase)) { n = n.Substring(article.Length); break; }
            if (n.Length == 0) return "\u2605 Rare Creature";
            var words = n.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
                words[i] = char.ToUpperInvariant(words[i][0]) + words[i].Substring(1);
            return "\u2605 " + string.Join(" ", words);
        }

        /// <summary>Bodies some creature rule names, so most mobiles skip the rules.</summary>
        public static bool AnyMobileRules { get { MaybeReload(); return _rareMobiles.Length > 0; } }

        public static LooseStyle LooseTown { get { MaybeReload(); return _looseTown; } }
        public static LooseStyle LooseHouse { get { MaybeReload(); return _looseHouse; } }
        /// <summary>The guard-zone file in Data/Client that says where the towns are.</summary>
        public static string LooseZones { get { MaybeReload(); return _looseZones; } }

        public static HighlightGrade RareContainer(uint serial)
        {
            MaybeReload();
            return _rareContainers.TryGetValue(serial, out HighlightGrade g) ? g : HighlightGrade.None;
        }

        public static bool HasRareContainers { get { MaybeReload(); return _rareContainers.Count > 0; } }

        /// <summary>Whether any rule reaches fixed ground items, so their names are worth asking for.</summary>
        public static bool HasLandmarks
        {
            get
            {
                MaybeReload();
                foreach (RareRule rule in _rareRules)
                    if (rule.Landmark) return true;
                return false;
            }
        }

        private static (string Name, ushort Hue, int Tier)[] ByLength((string Name, ushort Hue, int Tier)[] colors)
        {
            var sorted = ((string Name, ushort Hue, int Tier)[])colors.Clone();
            Array.Sort(sorted, (a, b) => b.Name.Length.CompareTo(a.Name.Length));
            return sorted;
        }

        public static int RareGraphicCount { get { MaybeReload(); return _rareGraphics.Count; } }

        /// <summary>True when the item's OPL name matches a shard-custom rare.</summary>
        public static bool IsRareName(string name)
        {
            MaybeReload();
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string n in _rareNames)
                if (name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static bool IsRare(ushort graphic)
        {
            MaybeReload();
            return _rareGraphics.Contains(graphic);
        }

        public static Color Slayer(string family)
        {
            MaybeReload();
            return _slayers.TryGetValue(family, out Color c) ? c : Color.White;
        }

        public static bool TryWand(string type, out Color color)
        {
            MaybeReload();
            return _wands.TryGetValue(type, out color);
        }

        /// <summary>Border colour for a potion keg's family; "unknown" for anything unlisted.</summary>
        public static Color Keg(string family)
        {
            MaybeReload();
            if (family != null && _kegs.TryGetValue(family, out Color c)) return c;
            return _kegs.TryGetValue("unknown", out Color u) ? u : DefaultKegs["unknown"];
        }

        /// <summary>Charge-type keys, longest first so "greater healing" beats "healing".</summary>
        public static IEnumerable<string> WandKeysByLength()
        {
            MaybeReload();
            var keys = new List<string>(_wands.Keys);
            keys.Sort((a, b) => b.Length.CompareTo(a.Length));
            return keys;
        }

        private static void MaybeReload()
        {
            DateTime now = DateTime.UtcNow;
            if (_loaded && (now - _lastCheck).TotalSeconds < RELOAD_INTERVAL_SEC)
                return;
            _lastCheck = now;

            try
            {
                // No file: the compiled defaults stand. The tracked copy lives in the
                // uo repo (configs/TazUO/Data/Client/appraisal_palette.json, symlinked
                // in); the client no longer writes one, because rewriting a symlinked
                // file would replace the link with a plain copy.
                if (!File.Exists(PalettePath))
                {
                    _loaded = true;
                    return;
                }

                DateTime mtime = File.GetLastWriteTimeUtc(PalettePath);
                if (_loaded && mtime == _lastMtime)
                    return;

                Load();
                _lastMtime = mtime;
                _loaded = true;
            }
            catch (IOException)
            {
                // Mid-write by an editor — try again on the next check.
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException)
            {
                // A typo mid-edit. Keep what was loaded before and don't retry until the
                // file changes again; one warning per broken save.
                _lastMtime = File.GetLastWriteTimeUtc(PalettePath);
                _loaded = true;
                Log.Warn("AppraisalPalette: " + ex.Message);
            }
        }

        private static void Load()
        {
            string json = File.ReadAllText(PalettePath);
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            var tiers = new List<Color>();
            if (root.TryGetProperty("tiers", out JsonElement te) && te.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in te.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.String && TryHex(e.GetString(), out Color c))
                        tiers.Add(c);
                }
            }
            _tiers = tiers.Count == DefaultTiers.Length ? tiers.ToArray() : (Color[])DefaultTiers.Clone();

            _unidentified = ReadColor(root, "unidentified", DefaultUnidentified);
            _durabilityPip = ReadColor(root, "durabilityPip", DefaultDurabilityPip);
            _genericWand = ReadColor(root, "genericWand", DefaultGenericWand);
            _containerOutline = ReadColor(root, "containerOutline", DefaultContainerOutline);

            var ramp = new List<Color>();
            if (root.TryGetProperty("capacityRamp", out JsonElement cr) && cr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement e in cr.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && TryHex(e.GetString(), out Color cc)) ramp.Add(cc);
            _capacityRamp = ramp.Count >= 2 ? ramp.ToArray() : (Color[])DefaultCapacityRamp.Clone();

            var rnames = new List<string>();
            if (root.TryGetProperty("rareNames", out JsonElement rn) && rn.ValueKind == JsonValueKind.Array)
                foreach (JsonElement e in rn.EnumerateArray())
                {
                    string v = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(v)) rnames.Add(v.Trim());
                }
            _rareNames = rnames.Count > 0 ? rnames.ToArray() : (string[])DefaultRareNames.Clone();

            var rares = new HashSet<ushort>();
            if (root.TryGetProperty("rareGraphics", out JsonElement re) && re.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in re.EnumerateArray())
                {
                    // Accept both 0x1234 strings and plain numbers, since a hand-edited
                    // list will naturally be written in hex.
                    if (e.ValueKind == JsonValueKind.String
                        && TryHexUShort(e.GetString(), out ushort gv)) rares.Add(gv);
                    else if (e.ValueKind == JsonValueKind.Number
                        && e.TryGetUInt16(out ushort nv)) rares.Add(nv);
                }
            }
            _rareGraphics = rares.Count > 0 ? rares : new HashSet<ushort>(DefaultRareGraphics);

            _topTierBorderBonus = root.TryGetProperty("topTierBorderBonus", out JsonElement tb)
                                  && tb.TryGetInt32(out int tbv) && tbv >= 0 && tbv <= 4
                                  ? tbv : DefaultTopTierBorderBonus;

            _slayers = ReadMap(root, "slayers", DefaultSlayers, StringComparer.Ordinal);
            _wands = ReadMap(root, "wands", DefaultWands, StringComparer.OrdinalIgnoreCase);
            _kegs = ReadMap(root, "kegs", DefaultKegs, StringComparer.OrdinalIgnoreCase);

            _grades = ReadGrades(root);
            _exceptional = ReadColor(root, "exceptional", DefaultExceptional);

            _pulsePeriod = DefaultPulsePeriod;
            _pulseFloor = DefaultPulseFloor;
            if (root.TryGetProperty("pulse", out JsonElement pe) && pe.ValueKind == JsonValueKind.Object)
            {
                _pulsePeriod = ReadFloat(pe, "period", DefaultPulsePeriod, 0.2f, 10f);
                _pulseFloor = ReadFloat(pe, "floor", DefaultPulseFloor, 0f, 1f);
            }

            _xrayChest = DefaultXrayChest;
            _xrayLooted = DefaultXrayLooted;
            _xrayCapacity = DefaultXrayCapacity;
            _xrayCulledRange = DefaultXrayCulledRange;
            if (root.TryGetProperty("xray", out JsonElement xe) && xe.ValueKind == JsonValueKind.Object)
            {
                _xrayChest = ReadFloat(xe, "chest", DefaultXrayChest, 0f, 1f);
                _xrayLooted = ReadFloat(xe, "looted", DefaultXrayLooted, 0f, 1f);
                _xrayCapacity = ReadFloat(xe, "capacity", DefaultXrayCapacity, 0f, 1f);
                _xrayCulledRange = (int)ReadFloat(xe, "culledRange", DefaultXrayCulledRange, 0f, 40f);
            }

            _chimeGap = ReadFloat(root, "chimeGap", DefaultChimeGap, 0f, 60f);

            var seeds = new List<(string, ushort, int)>();
            var overrides = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("seedColors", out JsonElement se) && se.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in se.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.Object) continue;
                    int hue = (int)ReadFloat(p.Value, "hue", -1, -1, 0xFFFF);
                    int tier = (int)ReadFloat(p.Value, "tier", 1, 1, 4);
                    if (hue < 0) continue;
                    string key = p.Name.Trim().ToLowerInvariant();
                    seeds.Add((key, (ushort)hue, tier));
                    if (TryHex(Str(p.Value, "color"), out Color oc)) overrides[key] = oc;
                }
            }
            _seedColors = seeds.Count > 0 ? seeds.ToArray() : DefaultSeedColors;
            _seedColorsByLength = ByLength(_seedColors);
            _seedOverride = overrides;
            _plainSeed = ReadColor(root, "plainSeed", DefaultPlainSeed);

            _plantTypes = ReadStrings(root, "plantTypes", DefaultPlantTypes);
            _bonsaiCategories = ReadStrings(root, "bonsaiCategories", DefaultBonsaiCategories);

            _bonusColors = ReadMap(root, "bonusColors", DefaultBonusColors, StringComparer.OrdinalIgnoreCase);
            _bonusHues = new Dictionary<string, ushort>(DefaultBonusHues, StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("bonusHues", out JsonElement bh) && bh.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty p in bh.EnumerateObject())
                    if (p.Value.TryGetUInt16(out ushort hv)) _bonusHues[p.Name] = hv;

            _rareRules = ReadRules(root);
            _rareMobiles = ReadMobileRules(root);

            _looseTown = DefaultLooseTown;
            _looseHouse = DefaultLooseHouse;
            _looseZones = DefaultLooseZones;
            if (root.TryGetProperty("loose", out JsonElement lo) && lo.ValueKind == JsonValueKind.Object)
            {
                _looseTown = ReadLoose(lo, "town", DefaultLooseTown);
                _looseHouse = ReadLoose(lo, "house", DefaultLooseHouse);
                string zones = Str(lo, "zones");
                if (zones != null) _looseZones = zones.Trim();
            }

            _rareContainers = new Dictionary<uint, HighlightGrade>(DefaultRareContainers);
            if (root.TryGetProperty("rareContainers", out JsonElement rc) && rc.ValueKind == JsonValueKind.Array)
            {
                _rareContainers.Clear();
                foreach (JsonElement e in rc.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.Object
                        && e.TryGetProperty("serial", out JsonElement sv) && sv.TryGetUInt32(out uint serial))
                    {
                        _rareContainers[serial] = ParseGrade(Str(e, "grade"), HighlightGrade.Rare);
                    }
                }
            }
        }

        private static Color ReadColor(JsonElement root, string name, Color fallback) =>
            TryHex(Str(root, name), out Color c) ? c : fallback;

        private static string Str(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

        private static float ReadFloat(JsonElement obj, string name, float fallback, float min, float max)
        {
            if (obj.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number
                && e.TryGetDouble(out double v) && v >= min && v <= max)
            {
                return (float)v;
            }
            return fallback;
        }

        private static string[] ReadStrings(JsonElement root, string name, string[] fallback)
        {
            var list = new List<string>();
            if (root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Array)
                foreach (JsonElement v in e.EnumerateArray())
                    if (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                        list.Add(v.GetString().Trim().ToLowerInvariant());
            return list.Count > 0 ? list.ToArray() : fallback;
        }

        private static HighlightGrade ParseGrade(string s, HighlightGrade fallback) =>
            Enum.TryParse(s, true, out HighlightGrade g) && g != HighlightGrade.None ? g : fallback;

        private static Dictionary<HighlightGrade, GradeStyle> ReadGrades(JsonElement root)
        {
            var map = new Dictionary<HighlightGrade, GradeStyle>(DefaultGrades);
            if (!root.TryGetProperty("grades", out JsonElement ge) || ge.ValueKind != JsonValueKind.Object)
                return map;

            foreach (JsonProperty p in ge.EnumerateObject())
            {
                HighlightGrade g = ParseGrade(p.Name, HighlightGrade.None);
                if (g == HighlightGrade.None || p.Value.ValueKind != JsonValueKind.Object) continue;

                GradeStyle d = DefaultGrades[g];
                JsonElement v = p.Value;
                map[g] = new GradeStyle(
                    TryHex(Str(v, "color"), out Color c) ? c : d.Color,
                    ReadFloat(v, "alpha", d.Alpha, 0f, 1f),
                    (int)ReadFloat(v, "weight", d.Weight, 1, 3),
                    v.TryGetProperty("label", out JsonElement le) && (le.ValueKind == JsonValueKind.True || le.ValueKind == JsonValueKind.False)
                        ? le.GetBoolean() : d.Label,
                    (ushort)ReadFloat(v, "labelHue", d.LabelHue, 0, 0xFFFF),
                    (ushort)ReadFloat(v, "sound", d.Sound, 0, 0xFFFF),
                    ReadFloat(v, "xray", d.Xray, 0f, 1f),
                    v.TryGetProperty("pulse", out JsonElement pe) && (pe.ValueKind == JsonValueKind.True || pe.ValueKind == JsonValueKind.False)
                        ? pe.GetBoolean() : d.Pulse);
            }
            return map;
        }

        private static LooseStyle ReadLoose(JsonElement obj, string name, LooseStyle d)
        {
            if (!obj.TryGetProperty(name, out JsonElement e) || e.ValueKind != JsonValueKind.Object)
                return d;
            return new LooseStyle(
                TryHex(Str(e, "color"), out Color c) ? c : d.Color,
                ReadFloat(e, "alpha", d.Alpha, 0f, 1f),
                ReadFloat(e, "xray", d.Xray, 0f, 1f));
        }

        private static bool? ReadBool(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out JsonElement e)
            && (e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False)
                ? e.GetBoolean() : null;

        private static HashSet<ushort> ReadGraphics(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out JsonElement ga) || ga.ValueKind != JsonValueKind.Array)
                return null;
            var graphics = new HashSet<ushort>();
            foreach (JsonElement g in ga.EnumerateArray())
            {
                if (g.ValueKind == JsonValueKind.String && TryHexUShort(g.GetString(), out ushort gv)) graphics.Add(gv);
                else if (g.ValueKind == JsonValueKind.Number && g.TryGetUInt16(out ushort nv)) graphics.Add(nv);
            }
            return graphics;
        }

        /// <summary>A hue list: numbers, "0x" strings, and [lo, hi] pairs for ranges.</summary>
        private static HueSet ReadHues(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out JsonElement ha) || ha.ValueKind != JsonValueKind.Array)
                return null;
            var set = new HueSet();
            foreach (JsonElement h in ha.EnumerateArray())
            {
                if (h.ValueKind == JsonValueKind.Array && h.GetArrayLength() == 2
                    && TryHue(h[0], out ushort lo) && TryHue(h[1], out ushort hi))
                    set.Range(lo, hi);
                else if (TryHue(h, out ushort v))
                    set.Range(v, v);
            }
            return set.Empty ? null : set;
        }

        private static bool TryHue(JsonElement e, out ushort hue)
        {
            hue = 0;
            if (e.ValueKind == JsonValueKind.String) return TryHexUShort(e.GetString(), out hue);
            return e.ValueKind == JsonValueKind.Number && e.TryGetUInt16(out hue);
        }

        /// <summary>A rule's grade: "none" is allowed and means "not a collectible".</summary>
        private static HighlightGrade RuleGrade(JsonElement e) =>
            string.Equals(Str(e, "grade"), "none", StringComparison.OrdinalIgnoreCase)
                ? HighlightGrade.None
                : ParseGrade(Str(e, "grade"), HighlightGrade.Rare);

        private static MobileRule[] ReadMobileRules(JsonElement root)
        {
            if (!root.TryGetProperty("rareMobiles", out JsonElement re) || re.ValueKind != JsonValueKind.Array)
                return DefaultRareMobiles;

            var rules = new List<MobileRule>();
            foreach (JsonElement e in re.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                string name = Str(e, "name");
                HashSet<ushort> bodies = ReadGraphics(e, "bodies");
                bool? hued = ReadBool(e, "hued");
                if (string.IsNullOrWhiteSpace(name) && (bodies == null || bodies.Count == 0)) continue;
                HighlightGrade g = RuleGrade(e);
                if (g == HighlightGrade.None) continue;
                rules.Add(new MobileRule(name?.Trim(), bodies, hued, g, Str(e, "label"),
                    ReadHues(e, "hues"), ReadHues(e, "exceptHues")));
            }
            return rules.ToArray();
        }

        private static RareRule[] ReadRules(JsonElement root)
        {
            if (!root.TryGetProperty("rareRules", out JsonElement re) || re.ValueKind != JsonValueKind.Array)
                return DefaultRareRules;

            var rules = new List<RareRule>();
            foreach (JsonElement e in re.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;

                string name = Str(e, "name");
                HashSet<ushort> graphics = ReadGraphics(e, "graphics");
                bool? onGround = ReadBool(e, "onGround");

                if (string.IsNullOrWhiteSpace(name) && (graphics == null || graphics.Count == 0)) continue;
                rules.Add(new RareRule(name?.Trim(), graphics, onGround,
                    RuleGrade(e), Str(e, "label"), ReadBool(e, "hued"),
                    ReadHues(e, "hues"), ReadHues(e, "exceptHues"), ReadBool(e, "landmark") == true));
            }
            return rules.ToArray();
        }

        private static Dictionary<string, Color> ReadMap(
            JsonElement root, string name, Dictionary<string, Color> fallback, StringComparer cmp)
        {
            var map = new Dictionary<string, Color>(fallback, cmp);
            if (root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String && TryHex(p.Value.GetString(), out Color c))
                        map[p.Name] = c;
                }
            }
            return map;
        }

        private static bool TryHexUShort(string s, out ushort value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            return ushort.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryHex(string s, out Color color)
        {
            color = Color.White;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().TrimStart('#');
            if (s.Length != 6) return false;
            if (!int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return false;
            color = new Color((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
            return true;
        }

    }

    /// <summary>How loud a collectible is; see AppraisalPalette's grade notes.</summary>
    internal enum HighlightGrade
    {
        None = 0,
        Trace,
        Rare,
        Prized,
        Callout,
    }

    internal sealed class GradeStyle
    {
        public readonly Color Color;
        public readonly float Alpha;
        /// <summary>Outline thickness in pixels, 1-3.</summary>
        public readonly int Weight;
        /// <summary>Show the world label even when the item carries no data text.</summary>
        public readonly bool Label;
        public readonly ushort LabelHue;
        /// <summary>First-sight chime for a ground item, 0 for none.</summary>
        public readonly ushort Sound;
        /// <summary>Opacity of the outline where a wall or ceiling hides it, 0 for none.</summary>
        public readonly float Xray;
        public readonly bool Pulse;

        public GradeStyle(Color color, float alpha, int weight, bool label, ushort labelHue, ushort sound, float xray, bool pulse)
        {
            Color = color;
            Alpha = alpha;
            Weight = weight;
            Label = label;
            LabelHue = labelHue;
            Sound = sound;
            Xray = xray;
            Pulse = pulse;
        }
    }

    internal sealed class RareRule
    {
        public readonly string Name;
        public readonly HashSet<ushort> Graphics;
        /// <summary>Null matches anywhere; false only off the ground; true only on it.</summary>
        public readonly bool? OnGround;
        public readonly HighlightGrade Grade;
        public readonly string Label;
        /// <summary>Null matches any hue; true only a hued item; false only an unhued one.</summary>
        public readonly bool? Hued;
        /// <summary>When set, the hue must be one of these.</summary>
        public readonly HueSet Hues;
        /// <summary>When set, the hue must be none of these.</summary>
        public readonly HueSet ExceptHues;
        /// <summary>
        /// Matches a ground item nobody can move, which every other rule skips as set
        /// dressing: a landmark worth finding (the silver tree), not a thing to carry off.
        /// </summary>
        public readonly bool Landmark;

        public RareRule(string name, HashSet<ushort> graphics, bool? onGround, HighlightGrade grade, string label,
            bool? hued = null, HueSet hues = null, HueSet exceptHues = null, bool landmark = false)
        {
            Landmark = landmark;
            Name = name;
            Graphics = graphics;
            OnGround = onGround;
            Grade = grade;
            Label = label;
            Hued = hued;
            Hues = hues;
            ExceptHues = exceptHues;
        }

        public bool Matches(string name, ushort graphic, bool onGround, ushort hue = 0)
        {
            if (OnGround.HasValue && OnGround.Value != onGround) return false;
            hue &= 0x7FFF;
            if (Hued.HasValue && Hued.Value != (hue != 0)) return false;
            if (Hues != null && !Hues.Contains(hue)) return false;
            if (ExceptHues != null && ExceptHues.Contains(hue)) return false;
            if (Graphics != null && Graphics.Count > 0 && !Graphics.Contains(graphic)) return false;
            return NameMatches(Name, name);
        }

        /// <summary>
        /// A rule's name against an item's: empty matches anything, "^text" the start of
        /// any line (a locked-down item's real name sits on the line after the label the
        /// merger promoted), anything else a substring. Case-insensitive.
        /// </summary>
        internal static bool NameMatches(string rule, string name)
        {
            if (string.IsNullOrEmpty(rule)) return true;
            if (string.IsNullOrEmpty(name)) return false;
            if (rule[0] != '^') return name.IndexOf(rule, StringComparison.OrdinalIgnoreCase) >= 0;

            string want = rule.Substring(1);
            foreach (string line in name.Split('\n'))
                if (line.TrimStart().StartsWith(want, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>A creature worth a callout: every condition given must hold.</summary>
    internal sealed class MobileRule
    {
        public readonly string Name;
        public readonly HashSet<ushort> Bodies;
        /// <summary>Null matches any hue; true only a hued body; false only an unhued one.</summary>
        public readonly bool? Hued;
        /// <summary>When set, the hue must be one of these.</summary>
        public readonly HueSet Hues;
        /// <summary>When set, the hue must be none of these: a species' ordinary colours.</summary>
        public readonly HueSet ExceptHues;
        public readonly HighlightGrade Grade;
        /// <summary>Null labels the creature by its own name (<see cref="AppraisalPalette.CreatureLabel"/>).</summary>
        public readonly string Label;

        public MobileRule(string name, HashSet<ushort> bodies, bool? hued, HighlightGrade grade, string label,
            HueSet hues = null, HueSet exceptHues = null)
        {
            Name = name;
            Bodies = bodies;
            Hued = hued;
            Hues = hues;
            ExceptHues = exceptHues;
            Grade = grade;
            Label = label;
        }

        public bool Matches(string name, ushort body, ushort hue)
        {
            if (Bodies != null && Bodies.Count > 0 && !Bodies.Contains(body)) return false;
            hue &= 0x7FFF;
            if (Hued.HasValue && Hued.Value != (hue != 0)) return false;
            if (Hues != null && !Hues.Contains(hue)) return false;
            if (ExceptHues != null && ExceptHues.Contains(hue)) return false;
            return RareRule.NameMatches(Name, name);
        }
    }

    /// <summary>Hues as single values and inclusive ranges.</summary>
    internal sealed class HueSet
    {
        private readonly List<(ushort Lo, ushort Hi)> _ranges = new();

        public HueSet(params ushort[] hues)
        {
            foreach (ushort h in hues) _ranges.Add((h, h));
        }

        public HueSet Range(ushort lo, ushort hi)
        {
            _ranges.Add(lo <= hi ? (lo, hi) : (hi, lo));
            return this;
        }

        public bool Empty => _ranges.Count == 0;

        public bool Contains(ushort hue)
        {
            foreach ((ushort lo, ushort hi) in _ranges)
                if (hue >= lo && hue <= hi) return true;
            return false;
        }
    }

    /// <summary>How a loose ground item in a town or a house is outlined.</summary>
    internal sealed class LooseStyle
    {
        public readonly Color Color;
        public readonly float Alpha;
        /// <summary>Opacity through walls, floors and ceilings, 0 for none.</summary>
        public readonly float Xray;

        public LooseStyle(Color color, float alpha, float xray)
        {
            Color = color;
            Alpha = alpha;
            Xray = xray;
        }
    }
}
