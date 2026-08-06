// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Configuration;
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
                Kind != AppraisalKind.None || Unidentified
                || DurabilityTier > 0 || Slayer != SlayerFamily.None || Rare;

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

            if (_cache.TryGetValue(serial, out Entry e))
            {
                if (e.Frozen)
                {
                    // Repair an entry that froze before the item was resolvable. Graphic is
                    // stamped from world.Items, which can miss in the first frames after a
                    // container opens, and rarity is computed FROM the graphic — so a 0
                    // there means rarity was never actually evaluated, and the item would
                    // stay unmarked forever. Symptom was two identical candelabra in one
                    // bag where only one carried the rare border.
                    if (e.Value.Graphic == 0
                        && world.Items.TryGetValue(serial, out GameObjects.Item late))
                    {
                        e.Value.Graphic = late.Graphic;
                    }
                    return Recolor(e.Value);
                }

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
                    return Recolor(e.Value);
            }

            if (!world.OPL.TryGetNameAndData(serial, out string name, out string data))
            {
                // Rarity is decided by GRAPHIC alone and needs no tooltip, so a missing
                // OPL entry must not suppress it. This is the common case in the world:
                // ground decorations only get OPL once something asks for it (a hover, or
                // a container gump laying them out), so gating rares behind OPL meant a
                // rare sitting on a table was never marked until pointed at.
                if (world.Items.TryGetValue(serial, out GameObjects.Item bare)
                    && AppraisalPalette.IsRare(bare.Graphic))
                {
                    return Recolor(new Result { Graphic = bare.Graphic });
                }
                return default;
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
            if (world.Items.TryGetValue(serial, out GameObjects.Item gi))
                r.Graphic = gi.Graphic;
            r = Recolor(r);

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
            e.Frozen = (e.Attempts >= 2 && r.HasAnything && !r.Unidentified)
                       || e.Attempts >= MAX_EMPTY_ATTEMPTS;

            return r;
        }

        /// <summary>
        /// Re-resolve colors on a cached result so live palette edits take effect without
        /// having to invalidate (and re-parse) every cached appraisal.
        /// </summary>
        private static Result Recolor(Result r)
        {
            // Re-checked here rather than cached, so appending to rareGraphics in the
            // palette file lights items up without invalidating any parse results.
            // Graphic OR name. Shard-custom rares reuse ordinary art — Marijuana is drawn
            // with the Nightshade graphic, shared with 144 ordinary reagents here — so the
            // graphic alone would either miss them or flag every reagent.
            r.Rare = (r.Graphic != 0 && AppraisalPalette.IsRare(r.Graphic))
                     || AppraisalPalette.IsRareName(r.Name);

            if (r.Unidentified && r.Kind != AppraisalKind.Wand)
                r.Outline = AppraisalPalette.Unidentified;
            else if (r.Kind == AppraisalKind.Weapon || r.Kind == AppraisalKind.Armor)
                r.Outline = AppraisalPalette.Tier(r.BorderTier);
            else if (r.Unidentified)
                r.Outline = AppraisalPalette.Unidentified;
            else
                r.Outline = null;

            // A rare carries no magic tier, so the border is free — claim it. If the item
            // somehow does have a tier, that wins the border and the corner mark still
            // identifies it as a rare.
            if (r.Rare && !r.Outline.HasValue)
                r.Outline = AppraisalPalette.RareOutline;

            return r;
        }

        private static Result Parse(string text)
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

            return Recolor(r);
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
            return r.Outline ?? r.WandColor;
        }
    }
}
