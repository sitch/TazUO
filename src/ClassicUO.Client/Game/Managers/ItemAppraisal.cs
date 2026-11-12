// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.Managers
{
    internal enum AppraisalKind
    {
        None = 0,
        Weapon,
        Armor,
        Wand,
    }

    /// <summary>Slayer groupings. Lesser slayers roll up to the super slayer covering them.</summary>
    internal enum SlayerFamily
    {
        None = 0,
        Undead,      // Silver
        Repond,      // orc / troll / ogre
        Reptilian,   // dragon / snake / lizardman / ophidian
        Demonic,     // daemon / gargoyle / balron
        Arachnid,    // spider / scorpion / terathan
        Elemental,   // flame / water / air / earth / blood / poison
    }

    /// <summary>
    /// Parses an item's merged OPL text into the qualities worth showing. One appraisal
    /// pass, consumed by every render surface; all COLORS live in AppraisalPalette so
    /// they can be tuned without a rebuild.
    ///
    /// The vocabulary is not guessed — it was read out of the shard's own cliloc.enu,
    /// where all of it sits in contiguous ordered blocks:
    ///
    ///   1038000       Unidentified
    ///   1038001-05    Durability   Durable .. Indestructable
    ///   1038006-10    Armor AR     Defense .. Invulnerability
    ///   1038011-15    Accuracy     Accurate .. Supremely Accurate
    ///   1038016-20    Damage       Ruin .. Vanquishing
    ///   1017324-71    Wand charge types
    ///   1017384-409   Slayers      Silver .. Elemental Ban
    ///   1018303       Exceptional  (of "Item quality: Exceptional/Average/Low")
    ///
    /// Note "Indestructable" is the shard's spelling, sic — matching the correct spelling
    /// would silently never fire.
    ///
    /// CHANNEL ASSIGNMENT — one meaning per element, no sharing:
    ///   outer border   tier (weapon damage/accuracy, or armor AR), or unidentified
    ///   inset border   Exceptional
    ///   top pips       durability
    ///   bottom pips    accuracy
    ///   left bar       wand charge type
    ///   right bar      slayer family
    ///   corner square  collector grade (Trace .. Callout)
    ///   top-right text seed type index, bonsai star, or stat/skill bonus
    /// A collectible with no magic tier takes the outer border too, in its ACCENT
    /// colour: a seed's own colour, a bonus's stat colour, else the grade colour.
    /// Wand type used to share the border with tier, which forced 5 ordinal colors and
    /// 13 categorical ones into one channel and made collisions unavoidable. Giving it
    /// its own element is what lets both palettes be chosen freely.
    /// </summary>
    internal static class ItemAppraisal
    {
        // ── Tier vocabularies, index 0 => tier 1 ────────────────────────────────────
        private static readonly string[] DamageTiers =
            { "Ruin", "Might", "Force", "Power", "Vanquishing" };

        private static readonly string[] AccuracyTiers =
            { "Accurate", "Surpassingly Accurate", "Eminently Accurate",
              "Exceedingly Accurate", "Supremely Accurate" };

        private static readonly string[] ArmorTiers =
            { "Defense", "Guarding", "Hardening", "Fortification", "Invulnerability" };

        private static readonly string[] DurabilityTiers =
            { "Durable", "Substantial", "Massive", "Fortified", "Indestructable" };

        // ── Slayers (cliloc 1017384-1017409) ────────────────────────────────────────
        // Six families. The six SUPER slayers cover a whole creature family and are far
        // more valuable than the lesser ones, so rank rides on bar LENGTH while family
        // rides on color.
        private static readonly (string Token, SlayerFamily Family, bool Super)[] Slayers =
        {
            ("Silver",              SlayerFamily.Undead,    true),
            ("Repond",              SlayerFamily.Repond,    true),
            ("Reptilian Death",     SlayerFamily.Reptilian, true),
            ("Exorcism",            SlayerFamily.Demonic,   true),
            ("Arachnid Doom",       SlayerFamily.Arachnid,  true),
            ("Elemental Ban",       SlayerFamily.Elemental, true),

            ("Orc Slaying",         SlayerFamily.Repond,    false),
            ("Troll Slaughter",     SlayerFamily.Repond,    false),
            ("Ogre Thrashing",      SlayerFamily.Repond,    false),

            ("Dragon Slaying",      SlayerFamily.Reptilian, false),
            ("Snake's Bane",        SlayerFamily.Reptilian, false),
            ("Lizardman Slaughter", SlayerFamily.Reptilian, false),
            ("Ophidian",            SlayerFamily.Reptilian, false),

            ("Daemon Dismissal",    SlayerFamily.Demonic,   false),
            ("Gargoyle's Foe",      SlayerFamily.Demonic,   false),
            ("Balron Damnation",    SlayerFamily.Demonic,   false),

            ("Terathan",            SlayerFamily.Arachnid,  false),
            ("Spider's Death",      SlayerFamily.Arachnid,  false),
            ("Scorpion's Bane",     SlayerFamily.Arachnid,  false),

            ("Flame Dousing",       SlayerFamily.Elemental, false),
            ("Water Dissipation",   SlayerFamily.Elemental, false),
            ("Vacuum",              SlayerFamily.Elemental, false),
            ("Elemental Health",    SlayerFamily.Elemental, false),
            ("Earth Shatter",       SlayerFamily.Elemental, false),
            ("Blood Drinking",      SlayerFamily.Elemental, false),
            ("Summer Wind",         SlayerFamily.Elemental, false),
        };

        public static Color FamilyColor(SlayerFamily f) => AppraisalPalette.Slayer(f.ToString());

        public static bool Enabled =>
            ProfileManager.CurrentProfile != null
            && ProfileManager.CurrentProfile.HighlightAppraisedItems;

        public struct Result
        {
            public AppraisalKind Kind;
            public int DamageTier;
            public int AccuracyTier;
            public int ArmorTier;
            public int DurabilityTier;
            public bool Unidentified;
            public bool Exceptional;
            public SlayerFamily Slayer;
            public bool SuperSlayer;
            /// <summary>Charge-type key (e.g. "identification"), or null when not a wand.</summary>
            public string WandType;
            /// <summary>Art graphic, needed because rares are matched by graphic, not text.</summary>
            public ushort Graphic;
            /// <summary>Collector rare, by graphic OR by name (see AppraisalPalette).</summary>
            public bool Rare;
            /// <summary>
            /// Text used for name-based rare matching, kept so it re-evaluates when the
            /// palette's rareNames list is edited live.
            ///
            /// This is the OPL Name PLUS the first line of Data, because a locked-down
            /// item does not keep its real name in Name: the label merger promotes the
            /// "locked down" label there and the actual name is pushed into Data. 786
            /// items in this player's own database are in that state, so matching Name
            /// alone silently missed every rare locked down in the house.
            /// </summary>
            public string Name;
            /// <summary>Border color: tier or unidentified. Null for a plain wand.</summary>
            public Color? Outline;

            /// <summary>
            /// The tier that decides the border colour. DAMAGE only for weapons (armor
            /// rating for armor) — accuracy deliberately does NOT feed it.
            ///
            /// An earlier version used max(damage, accuracy) so a Ruin / Supremely
            /// Accurate weapon wouldn't render as junk. In practice that made a Force
            /// warhammer and a Ruin heavy crossbow both come out blue, which is worse:
            /// the loudest channel stopped meaning one thing. Accuracy is fully carried
            /// by the bottom pips instead — count AND colour — which is unambiguous.
            ///
            /// Weapons with no damage tier at all (e.g. "[Silver/Accurate]") fall back to
            /// tier 1 rather than going unmarked.
            /// </summary>
            public int BorderTier => Kind == AppraisalKind.Armor
                ? Math.Max(ArmorTier, 1)
                : Math.Max(DamageTier, 1);

            public bool HasAnything =>
                Kind != AppraisalKind.None || Unidentified || Exceptional
                || DurabilityTier > 0 || Slayer != SlayerFamily.None || Rare
                || SeedColor != null || Bonsai != null || BonusKey != null;

            // ── Where the item is, stamped on every call (never cached) ──────────────
            public ushort Hue;
            public bool OnGround;
            /// <summary>On the ground without the server's Movable flag: decoration or locked down.</summary>
            public bool Fixed;
            public bool IsContainer;
            /// <summary>Grade of a collector container somewhere above it, or None.</summary>
            public HighlightGrade RareContainer;

            // ── Seeds ────────────────────────────────────────────────────────────────
            /// <summary>Colour word from the name ("bright red"), null when not a seed.</summary>
            public string SeedColor;
            public ushort SeedHue;
            /// <summary>Colour rarity 1-4, see AppraisalPalette.DefaultSeedColors.</summary>
            public int SeedTier;
            /// <summary>1-based PlantType index, 0 while the type is unknown.</summary>
            public int PlantType;
            /// <summary>Bonsai category ("common", "exotic" ...), null otherwise.</summary>
            public string Bonsai;

            // ── Stat and skill bonuses ───────────────────────────────────────────────
            /// <summary>"str", "dex", "int" or "skill" for the first bonus line, else null.</summary>
            public string BonusKey;
            /// <summary>Every bonus, "+50 Tracking · +10 Animal Lore".</summary>
            public string BonusLabel;
            /// <summary>The first bonus, short enough for a grid cell ("+4 Int").</summary>
            public string BonusShort;
            public bool Blessed;

            // ── Collector grade, resolved in Recolor ─────────────────────────────────
            public HighlightGrade Grade;
            /// <summary>What it is, in its own colour: seed hue, bonus colour or grade colour.</summary>
            public Color? Accent;
            /// <summary>World label text, or null for none.</summary>
            public string Label;
            public ushort LabelHue;
            /// <summary>Top-right grid text, or null.</summary>
            public string Badge;
            public ushort BadgeHue;

            /// <summary>
            /// Potion family of a potion keg ("heal", "cure", ...), "unknown" for a keg
            /// whose contents can't be named, null when the item is not a potion keg.
            /// </summary>
            public string KegPotion;
            /// <summary>Keg fill, 0..1. Zero when empty OR not yet reported.</summary>
            public float KegLevel;

            public Color KegColor => AppraisalPalette.Keg(KegPotion);

            /// <summary>Left-bar color for a wand, from the live palette.</summary>
            public Color? WandColor
            {
                get
                {
                    if (WandType == null) return null;
                    return AppraisalPalette.TryWand(WandType, out Color c) ? c : AppraisalPalette.GenericWand;
                }
            }
        }

        // ── Cache ───────────────────────────────────────────────────────────────────
        // An item's properties are fixed for its lifetime with exactly one exception:
        // identification, which turns [Unidentified] into real tier text. So once an
        // identified item is appraised the answer can never change and we FREEZE it —
        // no re-parse, ever, regardless of how many MegaCliloc rebroadcasts arrive.
        //
        // That matters because this shard rebroadcasts OPL every 1-2 s and bumps the
        // revision each time; keying purely on revision meant re-parsing every visible
        // item about once a second forever.
        //
        // Colors are NOT cached here — they're read from AppraisalPalette at draw time,
        // so a palette edit shows up immediately without invalidating parse results.
        private sealed class Entry
        {
            public Result Value;
            public uint Revision;
            public int Attempts;
            public bool Frozen;
        }

        private static readonly Dictionary<uint, Entry> _cache = new();
        private const int MAX_EMPTY_ATTEMPTS = 3;
        private const int CACHE_LIMIT = 4096;

        public static void ClearCache() => _cache.Clear();

        public static Result Appraise(World world, uint serial)
        {
            if (!Enabled || world == null)
                return default;

            // Looked up once and stamped on every return path, cached or not: where an
            // item sits (ground, crate, pack) changes under a frozen parse. Graphic is
            // stamped here too, which also repairs an entry that froze in the first
            // frames after a container opened, before world.Items had the item. Rarity
            // is computed FROM the graphic, so a 0 there once left one of two identical
            // candelabra in a bag unmarked forever.
            world.Items.TryGetValue(serial, out Item item);
            Result r = ParseCached(world, serial, item);
            if (item != null)
                Stamp(world, item, ref r);

            // A piece whose tooltip stays bare even when worn: what it gave when the
            // appraise script tried it on. Applied after the cache, never into it, so
            // a new reading shows on the next frame.
            if (r.BonusKey == null && AppraisalTriedOn.TryLines(serial, out string[] tried))
                ParseBonuses(tried, ref r);
            return Recolor(r);
        }

        private static void Stamp(World world, Item item, ref Result r)
        {
            r.Graphic = item.Graphic;
            r.Hue = item.Hue;
            r.OnGround = item.OnGround;
            // Only a ground item's packet carries the flags byte; an item in a pack or
            // on a paperdoll always reads 0, so Movable means nothing off the ground.
            r.Fixed = item.OnGround && !item.IsMovable;
            r.IsContainer = item.ItemData.IsContainer;
            r.RareContainer = HighlightGrade.None;
            if (!AppraisalPalette.HasRareContainers)
                return;

            uint c = item.Container;
            for (int depth = 0; depth < 8 && SerialHelper.IsValid(c); depth++)
            {
                HighlightGrade g = AppraisalPalette.RareContainer(c);
                if (g != HighlightGrade.None)
                {
                    r.RareContainer = g;
                    return;
                }
                if (!world.Items.TryGetValue(c, out Item parent))
                    return;
                c = parent.Container;
            }
        }

        private static Result ParseCached(World world, uint serial, Item item)
        {
            if (_cache.TryGetValue(serial, out Entry e))
            {
                if (e.Frozen)
                    return e.Value;

                // Not frozen yet: reuse only while the OPL text is genuinely unchanged.
                //
                // An earlier version had a "cheap path" here that reused a cached
                // *unidentified* result without checking the revision, on the theory that
                // such an item can only change by being identified. That was wrong, and
                // badly so: OPL arrives in PIECES — MegaCliloc supplies Data (which holds
                // "Mana Drain Charges: 28") while the single-click label supplies
                // "[Unidentified]" — and they land in either order. An item first seen
                // with only the label parsed as "unidentified, not a wand", and the cheap
                // path then returned that verdict forever, so the charges arriving a
                // moment later were never seen. Every wand in the pack stuck grey.
                if (world.OPL.TryGetRevision(serial, out uint rev) && rev == e.Revision)
                    return e.Value;
            }

            if (!world.OPL.TryGetNameAndData(serial, out string name, out string data))
            {
                // Rarity by graphic, rule, crate or seed name needs no tooltip, so a
                // missing OPL entry must not suppress it. This is the common case in the
                // world: ground decorations only get OPL once something asks for it (a
                // hover, or a container gump laying them out), so gating rares behind OPL
                // meant a rare sitting on a table was never marked until pointed at. The
                // entity name, when the server sent one, stands in for the OPL name.
                var bare = new Result { Name = item?.Name };
                ParseSeed(item?.Name, ref bare);
                return bare;
            }

            Result r = Parse((name ?? string.Empty) + "\n" + (data ?? string.Empty));
            // Name + first line of Data — see Result.Name for why Data matters here.
            // Only the first line, to keep cached entries small; the real name is always
            // the leading line when the merger has displaced it.
            string firstData = data;
            if (!string.IsNullOrEmpty(firstData))
            {
                int nl = firstData.IndexOf('\n');
                if (nl > 0) firstData = firstData.Substring(0, nl);
            }
            r.Name = string.IsNullOrEmpty(firstData) ? name : (name + "\n" + firstData);

            world.OPL.TryGetRevision(serial, out uint revision);

            if (e == null)
            {
                if (_cache.Count >= CACHE_LIMIT)
                    _cache.Clear();
                e = new Entry();
                _cache[serial] = e;
            }

            e.Value = r;
            e.Revision = revision;
            e.Attempts++;
            // Require TWO parses at different revisions before freezing a positive result.
            // Attempts only increments when the revision actually changed, so this
            // guarantees the OPL had a further chance to fill in before we commit to an
            // answer forever — the partial-text trap that produced the stuck grey wands.
            // Results that find nothing still give up after MAX_EMPTY_ATTEMPTS.
            //
            // Potion kegs never freeze: pouring changes the fill level, so the answer is
            // only ever good until the next label burst bumps the revision.
            e.Frozen = r.KegPotion == null
                       && ((e.Attempts >= 2 && r.HasAnything && !r.Unidentified)
                           || e.Attempts >= MAX_EMPTY_ATTEMPTS);

            return r;
        }

        /// <summary>
        /// Re-resolve colors and grade on a cached result, so live palette edits and the
        /// item's current place take effect without invalidating (and re-parsing) every
        /// cached appraisal. Reads only parsed and stamped fields, never its own outputs.
        /// </summary>
        internal static Result Recolor(Result r)
        {
            r.Grade = GradeOf(r, out string ruleLabel);
            r.Rare = r.Grade != HighlightGrade.None;

            if (r.Unidentified && r.Kind != AppraisalKind.Wand)
                r.Outline = AppraisalPalette.Unidentified;
            else if (r.Kind == AppraisalKind.Weapon || r.Kind == AppraisalKind.Armor)
                r.Outline = AppraisalPalette.Tier(r.BorderTier);
            else if (r.Unidentified)
                r.Outline = AppraisalPalette.Unidentified;
            else
                r.Outline = null;

            r.Accent = null;
            r.Label = null;
            r.Badge = null;
            r.LabelHue = 0;
            r.BadgeHue = 0;
            if (!r.Rare)
                return r;

            GradeStyle style = AppraisalPalette.Grade(r.Grade);

            // A collectible carries no magic tier, so the border is free: claim it in the
            // accent, which says WHAT it is, while the corner square says how rare. An
            // item that does carry a tier keeps it on the border.
            //
            // Exceptional no longer borrows a border here. It used to take tier 1's
            // colour when nothing else had, which put an exceptional Ruin weapon and a
            // plain exceptional one in the same bone white on the world outline. It has
            // its own copper now, applied by each surface (the grid's inset, the world's
            // lower edge).
            r.Accent = Accent(r, style);
            if (!r.Outline.HasValue)
                r.Outline = r.Accent;

            if (r.Bonsai != null)
            {
                r.Label = "\u2605 Bonsai \u00b7 " + r.Bonsai;
                r.Badge = "\u2605";
            }
            else if (r.SeedColor != null)
            {
                string colour = TitleCase(r.SeedColor);
                string[] types = AppraisalPalette.PlantTypes;
                if (r.PlantType > 0)
                {
                    r.Label = colour + " \u00b7 " + r.PlantType + "/" + types.Length + " " + TitleCase(types[r.PlantType - 1]);
                    r.Badge = r.PlantType.ToString();
                }
                else
                {
                    r.Label = colour + " seed";
                }
            }
            else if (r.BonusKey != null)
            {
                r.Label = r.BonusLabel;
                r.Badge = r.BonusShort;
            }

            if (ruleLabel != null)
                r.Label = ruleLabel;
            else if (r.Label == null && style.Label)
                r.Label = DisplayName(r.Name);

            ushort hue = r.SeedColor != null && r.SeedHue != 0 ? r.SeedHue
                : r.BonusKey != null ? AppraisalPalette.BonusHue(r.BonusKey)
                : style.LabelHue;
            r.LabelHue = hue;
            r.BadgeHue = hue;
            return r;
        }

        private static HighlightGrade GradeOf(Result r, out string ruleLabel)
        {
            // A collectible nobody can pick up is set dressing: the candles and trays
            // nailed to tables around town, or a piece locked down in a house. It is
            // not a find, so no grade, no chime, no x-ray, whatever rule matches it.
            if (r.Fixed)
            {
                ruleLabel = null;
                return HighlightGrade.None;
            }

            // A collector rule is a deliberate call ("this hat is common") and wins outright.
            if (AppraisalPalette.MatchRule(r.Name, r.Graphic, r.OnGround, r.Hue, out HighlightGrade ruled, out ruleLabel))
                return ruled;

            var g = HighlightGrade.None;

            // Blessed with a bonus is the shard's magic clothing and jewelry. A bonus
            // without Blessed is a mask or a pair of mining gloves: worth a glance.
            if (r.BonusKey != null)
                g = r.Blessed ? HighlightGrade.Prized : HighlightGrade.Trace;

            // Graphic OR name. Shard-custom rares reuse ordinary art — Marijuana is drawn
            // with the Nightshade graphic, shared with 144 ordinary reagents here — so the
            // graphic alone would either miss them or flag every reagent.
            if ((r.Graphic != 0 && AppraisalPalette.IsRare(r.Graphic)) || AppraisalPalette.IsRareName(r.Name))
                g = Max(g, HighlightGrade.Rare);

            // Everything in a collector crate, but not the bags that hold it.
            if (r.RareContainer != HighlightGrade.None && !r.IsContainer)
                g = Max(g, r.RareContainer);

            if (r.Bonsai != null)
                g = Max(g, HighlightGrade.Callout);
            else if (r.SeedColor != null)
            {
                // Colour rarity sets the grade; a seed that knows its plant type is a
                // step up, since a typed seed is what a breeder actually wants.
                int step = Math.Clamp(r.SeedTier, 1, 4) + (r.PlantType > 0 ? 1 : 0);
                g = Max(g, (HighlightGrade)Math.Min(step, (int)HighlightGrade.Callout));
            }
            return g;
        }

        private static HighlightGrade Max(HighlightGrade a, HighlightGrade b) => a > b ? a : b;

        private static Color Accent(in Result r, GradeStyle style)
        {
            if (r.SeedColor != null)
                return AppraisalPalette.SeedOutline(r.SeedColor, r.SeedHue);
            if (r.BonusKey != null)
                return AppraisalPalette.BonusColor(r.BonusKey);
            return style.Color;
        }

        private static string TitleCase(string s) =>
            string.IsNullOrEmpty(s) ? s : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s);

        /// <summary>The item's own name for a label: the real-name line, minus any amount.</summary>
        internal static string DisplayName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string line = name;
            int nl = name.IndexOf('\n');
            if (nl >= 0)
            {
                // A locked-down item's first line is the merger's promoted label.
                string first = name.Substring(0, nl);
                line = first.IndexOf("locked down", StringComparison.OrdinalIgnoreCase) >= 0
                    ? name.Substring(nl + 1) : first;
            }
            int colon = line.IndexOf(" : ", StringComparison.Ordinal);
            if (colon > 0) line = line.Substring(0, colon);
            line = line.Trim();
            return line.Length == 0 ? null : TitleCase(line);
        }

        internal static Result Parse(string text)
        {
            var r = new Result();
            if (text.Length <= 1)
                return r;

            List<string> tokens = Tokenize(text);

            r.Unidentified = HasToken(tokens, "Unidentified");
            r.Exceptional = HasToken(tokens, "Exceptional");

            r.DamageTier = BestMatch(tokens, DamageTiers);
            r.AccuracyTier = BestMatch(tokens, AccuracyTiers);
            r.ArmorTier = BestMatch(tokens, ArmorTiers);
            r.DurabilityTier = BestMatch(tokens, DurabilityTiers);

            foreach ((string token, SlayerFamily family, bool super) in Slayers)
            {
                if (HasToken(tokens, token))
                {
                    r.Slayer = family;
                    r.SuperSlayer = super;
                    break;
                }
            }

            // Charge lines carry a value ("Identification Charges: 61"), so they're matched
            // on the raw text rather than as whole tokens. Longest key first so "greater
            // healing" wins over the "healing" it contains.
            r.WandType = MatchWand(text);
            if (r.WandType != null)
            {
                r.Kind = AppraisalKind.Wand;

                // A wand's charge line leaks its type whether or not the server considers
                // it identified ("Identification Charges: 162" is present either way), so
                // [Unidentified] carries no information we don't already have and would
                // only add a grey border competing with the type bar.
                //
                // Clearing it also means wands FREEZE in the cache on first sight: an
                // unidentified entry can never freeze, because it has to keep re-checking
                // whether it has flipped. With 913 chargeable items in the database that
                // per-frame scan was the single biggest remaining cost. The tooltip still
                // shows [Unidentified] — that text is the client's own OPL rendering and
                // is untouched by this.
                r.Unidentified = false;
            }
            else if (r.DamageTier > 0 || r.AccuracyTier > 0)
                r.Kind = AppraisalKind.Weapon;
            else if (r.ArmorTier > 0)
                r.Kind = AppraisalKind.Armor;
            else if (r.Slayer != SlayerFamily.None)
                r.Kind = AppraisalKind.Weapon;   // slayer with no readable tier still matters

            ParseKeg(text, ref r);

            // The name line, or the next one when the label merger has promoted
            // "locked down" over it.
            string[] lines = text.Split('\n');
            ParseSeed(lines[0], ref r);
            if (r.SeedColor == null && r.Bonsai == null && lines.Length > 1)
                ParseSeed(lines[1], ref r);
            ParseBonuses(lines, ref r);

            return r;
        }

        // ── Seeds ───────────────────────────────────────────────────────────────────
        // ServUO names a seed "<colour> seed", or "<colour> <plant type> seed" once its
        // type is known; a bonsai seed is named for its category ("common seed"). The
        // graphic, 0x0DCF, is shared with fishing weights, so the name decides. A name
        // with no known colour or category is not a plant seed ("marijuana seeds").
        internal static void ParseSeed(string line, ref Result r)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            string s = line.Trim().ToLowerInvariant();
            int colon = s.IndexOf(" : ", StringComparison.Ordinal);
            if (colon > 0) s = s.Substring(0, colon).TrimEnd();

            if (s.EndsWith(" seeds", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 6);
            else if (s.EndsWith(" seed", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 5);
            else return;

            s = StripLead(s);

            foreach (string category in AppraisalPalette.BonsaiCategories)
            {
                if (s == category)
                {
                    r.Bonsai = category;
                    return;
                }
            }

            foreach ((string colour, ushort hue, int tier) in AppraisalPalette.SeedColorsByLength())
            {
                if (s == colour || s.StartsWith(colour + " ", StringComparison.Ordinal))
                {
                    r.SeedColor = colour;
                    r.SeedHue = hue;
                    r.SeedTier = tier;
                    string type = s.Substring(colour.Length).Trim();
                    r.PlantType = type.Length == 0 ? 0 : Array.IndexOf(AppraisalPalette.PlantTypes, type) + 1;
                    return;
                }
            }
        }

        /// <summary>Drop a leading article or amount ("a ", "an ", "12 ").</summary>
        private static string StripLead(string s)
        {
            if (s.StartsWith("a ", StringComparison.Ordinal)) return s.Substring(2);
            if (s.StartsWith("an ", StringComparison.Ordinal)) return s.Substring(3);
            int i = 0;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            return i > 0 && i < s.Length && s[i] == ' ' ? s.Substring(i + 1) : s;
        }

        // ── Stat and skill bonuses ──────────────────────────────────────────────────
        // The shard writes stats as "Intelligence Bonus 4" and skills as "Tailoring +5",
        // "Mining Bonus +1" or "Tracking +50", one per line. Skill lines are checked
        // against the skill list, so a vendor's "[+5 through +10 Damage Bonus]" or any
        // other "+n" text never reads as a skill.
        private static readonly Regex StatBonus =
            new Regex(@"^(Strength|Dexterity|Intelligence) Bonus \+?(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex SkillBonus =
            new Regex(@"^([A-Za-z'/ ]+?)(?: Bonus)? \+(\d+)$", RegexOptions.Compiled);

        private static readonly HashSet<string> SkillNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Alchemy", "Anatomy", "Animal Lore", "Item Identification", "Arms Lore", "Parrying",
            "Begging", "Blacksmithy", "Blacksmithing", "Bowcraft", "Fletching", "Bowcraft/Fletching",
            "Peacemaking", "Camping", "Carpentry", "Cartography", "Cooking", "Detecting Hidden",
            "Discordance", "Enticement", "Evaluating Intelligence", "Evaluate Intelligence", "Healing",
            "Fishing", "Forensic Evaluation", "Herding", "Hiding", "Provocation", "Inscription",
            "Lockpicking", "Magery", "Magic Resistance", "Resisting Spells", "Tactics", "Snooping",
            "Musicianship", "Poisoning", "Archery", "Spirit Speak", "Stealing", "Tailoring",
            "Animal Taming", "Taste Identification", "Tinkering", "Tracking", "Veterinary",
            "Swordsmanship", "Mace Fighting", "Fencing", "Wrestling", "Lumberjacking", "Mining",
            "Meditation", "Stealth", "Remove Trap", "Necromancy", "Focus", "Chivalry", "Bushido",
            "Ninjitsu", "Spellweaving", "Mysticism", "Imbuing", "Throwing",
        };

        internal static void ParseBonuses(string[] lines, ref Result r)
        {
            List<string> parts = null;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Equals("Blessed", StringComparison.OrdinalIgnoreCase))
                {
                    r.Blessed = true;
                    continue;
                }

                string key, name, shortName;
                int value;
                Match m = StatBonus.Match(line);
                if (m.Success)
                {
                    key = m.Groups[1].Value.Substring(0, 3).ToLowerInvariant();
                    name = shortName = TitleCase(key);
                    value = int.Parse(m.Groups[2].Value);
                }
                else if ((m = SkillBonus.Match(line)).Success && SkillNames.Contains(m.Groups[1].Value.Trim()))
                {
                    key = "skill";
                    name = m.Groups[1].Value.Trim();
                    shortName = ShortSkill(name);
                    value = int.Parse(m.Groups[2].Value);
                }
                else
                {
                    continue;
                }

                if (value <= 0)
                    continue;

                parts ??= new List<string>(2);
                parts.Add("+" + value + " " + name);
                if (r.BonusKey == null)
                {
                    r.BonusKey = key;
                    r.BonusShort = "+" + value + " " + shortName;
                }
            }
            if (parts != null)
                r.BonusLabel = string.Join(" \u00b7 ", parts);
        }

        /// <summary>A skill name that fits a grid cell: "MR", "Mining", "Tail.".</summary>
        private static string ShortSkill(string name)
        {
            string[] words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 1)
            {
                var initials = new char[words.Length];
                for (int i = 0; i < words.Length; i++) initials[i] = char.ToUpperInvariant(words[i][0]);
                return new string(initials);
            }
            return name.Length <= 6 ? name : name.Substring(0, 4) + ".";
        }

        // ── Potion kegs ─────────────────────────────────────────────────────────────
        // Both facts arrive ONLY on the single-click label channel. The OPL name is no
        // help and actively misleads: a keg crafted empty keeps "An empty potion keg"
        // (1041641) as its name forever, so a Greater Explosion keg that is very full
        // still reads "An empty potion keg" on its first line. The fill line decides;
        // the name line is never consulted.

        // 1041620-1041639 "A keg of <Type> potions". Matched as substrings of the type
        // phrase, so "Total Refreshment" finds "refresh" and "Greater Heal" finds "heal".
        private static readonly (string Word, string Family)[] KegPotionFamilies =
        {
            ("nightsight", "nightsight"),
            ("cure", "cure"),
            ("agility", "agility"),
            ("strength", "strength"),
            ("poison", "poison"),
            ("refresh", "refresh"),
            ("heal", "heal"),
            ("explosion", "explosion"),
        };

        // 1041611-1041618 "A keg of <colour> liquid." — a keg whose potion the viewer
        // can't name. The block is ordered by potion family (black = nightsight ...
        // purple = explosion), the same order as the potion clilocs, so the colour of
        // the liquid IS the family and an unnamed keg colours like a named one.
        private static readonly (string Word, string Family)[] KegLiquidFamilies =
        {
            ("black", "nightsight"),
            ("orange", "cure"),
            ("blue", "agility"),
            ("white", "strength"),
            ("green", "poison"),
            ("red", "refresh"),
            ("yellow", "heal"),
            ("purple", "explosion"),
        };

        // 502246-502258, the twelve fill lines. Each maps to the fraction the phrase
        // itself states; the shard only ever reports these buckets, never a count.
        private static readonly (string Phrase, float Level)[] KegFillLevels =
        {
            ("The keg is empty", 0f),
            ("The keg is nearly empty", 0.05f),
            ("The keg is not very full", 0.12f),
            ("The keg is about one quarter full", 0.25f),
            ("The keg is about one third full", 0.33f),
            ("The keg is almost half full", 0.43f),
            ("The keg is approximately half full", 0.5f),
            ("The keg is more than half full", 0.62f),
            ("The keg is about three quarters full", 0.75f),
            ("The keg is very full", 0.88f),
            ("The liquid is almost to the top of the keg", 0.97f),
            ("The keg is completely full", 1f),
        };

        internal static void ParseKeg(string text, ref Result r)
        {
            if (text.IndexOf("potion keg", StringComparison.OrdinalIgnoreCase) < 0
                && text.IndexOf("keg of ", StringComparison.OrdinalIgnoreCase) < 0)
                return;

            r.KegPotion = MatchKegType(text, " potions", KegPotionFamilies)
                          ?? MatchKegType(text, " liquid", KegLiquidFamilies)
                          ?? "unknown";

            foreach ((string phrase, float level) in KegFillLevels)
            {
                if (text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    r.KegLevel = level;
                    break;
                }
            }
        }

        /// <summary>Family named between "keg of " and the suffix, or null.</summary>
        private static string MatchKegType(string text, string suffix, (string Word, string Family)[] table)
        {
            int start = text.IndexOf("keg of ", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return null;
            start += "keg of ".Length;
            int end = text.IndexOf(suffix, start, StringComparison.OrdinalIgnoreCase);
            if (end < 0) return null;

            string type = text.Substring(start, end - start);
            foreach ((string word, string family) in table)
            {
                if (type.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                    return family;
            }
            return null;
        }

        /// <summary>
        /// Split the merged OPL text into comparable tokens: lines, bracket groups, and
        /// the slash-separated members inside a group ("[Exceptional/Massive/Ruin]").
        ///
        /// Whole-token equality rather than substring matching is what makes this safe.
        /// "Force", "Might", "Power" and especially "Silver" are ordinary English that
        /// appear inside item NAMES — silver ingot, silver necklace, silver-etched mace
        /// would every one of them false-positive on a substring scan. As tokens,
        /// "silver ingot" simply isn't equal to "Silver", and the collision disappears.
        /// Verified against the client's own items.db: 26 items carry "silver" in the
        /// name, and only the 6 real Silver slayers match.
        /// </summary>
        private static List<string> Tokenize(string text)
        {
            var outList = new List<string>(16);
            foreach (string raw in text.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = raw.Trim();
                if (t.Length > 0)
                    outList.Add(t);
            }
            return outList;
        }

        private static readonly char[] TokenSeparators = { '\n', '[', ']', '/', ':', '<', '>' };

        private static bool HasToken(List<string> tokens, string want)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                if (string.Equals(tokens[i], want, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Highest tier whose word appears as a token. 0 when none do.</summary>
        private static int BestMatch(List<string> tokens, string[] tiers)
        {
            // High tier to low so the most specific phrase wins where they nest —
            // "Supremely Accurate" must beat the bare "Accurate" it contains.
            for (int i = tiers.Length - 1; i >= 0; i--)
            {
                if (HasToken(tokens, tiers[i]))
                    return i + 1;
            }
            return 0;
        }

        private static string MatchWand(string text)
        {
            foreach (string key in AppraisalPalette.WandKeysByLength())
            {
                if (text.IndexOf(key + " charges", StringComparison.OrdinalIgnoreCase) >= 0)
                    return key;
            }
            if (text.IndexOf("charges", StringComparison.OrdinalIgnoreCase) >= 0)
                return string.Empty;   // chargeable, type unknown -> generic color
            return null;
        }

        /// <summary>
        /// Single color for surfaces that have only one channel — the world view and the
        /// classic container gump, where there are no edge bars. There the wand type has
        /// to ride on the outline, since it's the only thing available.
        /// </summary>
        public static Color? OutlineFor(World world, uint serial)
        {
            Result r = Appraise(world, serial);
            return r.Outline ?? r.WandColor ?? (r.Exceptional ? AppraisalPalette.Exceptional : null);
        }
    }
}
