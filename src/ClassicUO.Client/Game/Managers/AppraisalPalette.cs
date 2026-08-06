// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
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
    /// The file is written out with the built-in defaults if it doesn't exist, so it is
    /// self-documenting: look at it to see every knob.
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

        // Rares carry no magic tier, so the outer border is free for them. A hue well
        // clear of the tier ramp, since "collectible" is a different claim from "powerful".
        private static readonly Color DefaultRareOutline = new Color(0xFF, 0x6F, 0xA8);

        // ── Live values ──────────────────────────────────────────────────────────────
        private static Color[] _tiers = (Color[])DefaultTiers.Clone();
        private static Dictionary<string, Color> _slayers = new(DefaultSlayers);
        private static Dictionary<string, Color> _wands = new(DefaultWands, StringComparer.OrdinalIgnoreCase);
        private static Color _unidentified = DefaultUnidentified;
        private static Color _durabilityPip = DefaultDurabilityPip;
        private static Color _genericWand = DefaultGenericWand;
        private static Color _containerOutline = DefaultContainerOutline;
        private static Color[] _capacityRamp = (Color[])DefaultCapacityRamp.Clone();
        private static string[] _rareNames = (string[])DefaultRareNames.Clone();
        private static int _topTierBorderBonus = DefaultTopTierBorderBonus;
        private static HashSet<ushort> _rareGraphics = new(DefaultRareGraphics);
        private static Color _rareOutline = DefaultRareOutline;

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
        public static Color RareOutline { get { MaybeReload(); return _rareOutline; } }

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
                if (!File.Exists(PalettePath))
                {
                    WriteDefaults();
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
                    if (TryHex(e.GetString(), out Color c))
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
                    if (TryHex(e.GetString(), out Color cc)) ramp.Add(cc);
            _capacityRamp = ramp.Count >= 2 ? ramp.ToArray() : (Color[])DefaultCapacityRamp.Clone();
            _rareOutline = ReadColor(root, "rareOutline", DefaultRareOutline);

            var rnames = new List<string>();
            if (root.TryGetProperty("rareNames", out JsonElement rn) && rn.ValueKind == JsonValueKind.Array)
                foreach (JsonElement e in rn.EnumerateArray())
                {
                    string v = e.GetString();
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

            // A file written by an earlier build is missing any key added since, and the
            // user would never learn those knobs exist. Rewrite once, merging what was
            // loaded (so hand edits survive) with defaults for whatever was absent.
            if (!root.TryGetProperty("rareGraphics", out _)
                || !root.TryGetProperty("rareNames", out _)
                || !root.TryGetProperty("capacityRamp", out _)
                || !root.TryGetProperty("topTierBorderBonus", out _)
                || !root.TryGetProperty("containerOutline", out _))
            {
                WriteLive();
            }
        }

        private static Color ReadColor(JsonElement root, string name, Color fallback) =>
            root.TryGetProperty(name, out JsonElement e) && TryHex(e.GetString(), out Color c) ? c : fallback;

        private static Dictionary<string, Color> ReadMap(
            JsonElement root, string name, Dictionary<string, Color> fallback, StringComparer cmp)
        {
            var map = new Dictionary<string, Color>(fallback, cmp);
            if (root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    if (TryHex(p.Value.GetString(), out Color c))
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

        private static string Hex(Color c) => string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);

        /// <summary>Write the live values back out, preserving hand edits while adding
        /// any keys the file was missing.</summary>
        private static void WriteLive()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"_channels\": \"border=tier | left bar=wand type | right bar=slayer | top pips=durability | bottom pips=accuracy | inset border=Exceptional | top-left square=rare\",");
                sb.AppendLine("  \"_tierNames\": \"Basic, Uncommon, Rare, Legendary, Exotic (Destiny rarity order)\",");
                sb.AppendLine("  \"tiers\": [");
                for (int i = 0; i < _tiers.Length; i++)
                    sb.AppendFormat("    \"{0}\"{1}\n", Hex(_tiers[i]), i < _tiers.Length - 1 ? "," : "");
                sb.AppendLine("  ],");
                sb.AppendFormat("  \"unidentified\": \"{0}\",\n", Hex(_unidentified));
                sb.AppendFormat("  \"durabilityPip\": \"{0}\",\n", Hex(_durabilityPip));
                sb.AppendFormat("  \"genericWand\": \"{0}\",\n", Hex(_genericWand));
                sb.AppendFormat("  \"containerOutline\": \"{0}\",\n", Hex(_containerOutline));
                sb.AppendFormat("  \"topTierBorderBonus\": {0},\n", _topTierBorderBonus);
                sb.AppendFormat("  \"rareOutline\": \"{0}\",\n", Hex(_rareOutline));
                sb.AppendLine("  \"_rareNamesNote\": \"For shard-custom rares that REUSE common art (Marijuana uses the Nightshade graphic, a Water Bong uses bottle art) the graphic cannot identify them - match the name instead. Case-insensitive substring.\",");
                sb.AppendFormat("  \"rareNames\": [{0}],\n",
                    string.Join(", ", System.Array.ConvertAll(DefaultRareNames, n => "\"" + n + "\"")));
                sb.AppendFormat("  \"rareNames\": [{0}],\n",
                    string.Join(", ", System.Array.ConvertAll(_rareNames, n => "\"" + n + "\"")));
                sb.AppendLine("  \"_rareGraphicsNote\": \"Rares are matched by art graphic; nothing in the tooltip marks them. Append as you find more.\",");
                var rl = new List<ushort>(_rareGraphics); rl.Sort();
                sb.AppendFormat("  \"rareGraphics\": [{0}],\n",
                    string.Join(", ", rl.ConvertAll(g => "\"0x" + g.ToString("X4") + "\"")));
                sb.AppendLine();
                AppendMap(sb, "slayers", _slayers, true);
                AppendMap(sb, "wands", _wands, false);
                sb.AppendLine("}");
                File.WriteAllText(PalettePath, sb.ToString());
                _lastMtime = File.GetLastWriteTimeUtc(PalettePath);
            }
            catch (Exception ex)
            {
                Log.Warn("AppraisalPalette: could not upgrade palette file - " + ex.Message);
            }
        }

        private static void WriteDefaults()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PalettePath));

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"_comment\": \"Live-reloaded ~2s after save; no client restart needed.\",");
                sb.AppendLine("  \"_channels\": \"border=tier | left bar=wand type | right bar=slayer | top pips=durability | bottom pips=accuracy | inset border=Exceptional\",");
                sb.AppendLine("  \"_tierNames\": \"Basic, Uncommon, Rare, Legendary, Exotic (Destiny rarity order)\",");
                sb.AppendLine("  \"_destinyVerbatim\": [\"#C3BCB4\", \"#366F42\", \"#5076A3\", \"#522F65\", \"#CEAE33\"],");
                sb.AppendLine("  \"_destinyVerbatimNote\": \"Destiny's shipped fill colors. Paste over \\\"tiers\\\" for the authentic look; Uncommon and Legendary will be very dim as outlines.\",");
                sb.AppendLine();
                sb.AppendLine("  \"tiers\": [");
                for (int i = 0; i < DefaultTiers.Length; i++)
                {
                    sb.AppendFormat("    \"{0}\"{1}\n", Hex(DefaultTiers[i]),
                        i < DefaultTiers.Length - 1 ? "," : "");
                }
                sb.AppendLine("  ],");
                sb.AppendFormat("  \"unidentified\": \"{0}\",\n", Hex(DefaultUnidentified));
                sb.AppendFormat("  \"durabilityPip\": \"{0}\",\n", Hex(DefaultDurabilityPip));
                sb.AppendFormat("  \"genericWand\": \"{0}\",\n", Hex(DefaultGenericWand));
                sb.AppendFormat("  \"containerOutline\": \"{0}\",\n", Hex(DefaultContainerOutline));
                sb.AppendLine("  \"_capacityRampNote\": \"Container fill colour by WEIGHT (log scale to 100000 stones); the gradient LEVEL is item count.\",");
                sb.AppendFormat("  \"capacityRamp\": [{0}],\n",
                    string.Join(", ", System.Array.ConvertAll(DefaultCapacityRamp, c => "\"" + Hex(c) + "\"")));
                sb.AppendFormat("  \"topTierBorderBonus\": {0},\n", DefaultTopTierBorderBonus);
                sb.AppendFormat("  \"rareOutline\": \"{0}\",\n", Hex(DefaultRareOutline));
                sb.AppendLine("  \"_rareGraphicsNote\": \"Rares are matched by art graphic, since nothing in the tooltip marks them. Add more as you find them.\",");
                sb.AppendFormat("  \"rareGraphics\": [{0}],\n",
                    string.Join(", ", System.Array.ConvertAll(DefaultRareGraphics, g => "\"0x" + g.ToString("X4") + "\"")));
                sb.AppendLine();
                AppendMap(sb, "slayers", DefaultSlayers, true);
                AppendMap(sb, "wands", DefaultWands, false);
                sb.AppendLine("}");

                File.WriteAllText(PalettePath, sb.ToString());
                _lastMtime = File.GetLastWriteTimeUtc(PalettePath);
            }
            catch (Exception ex)
            {
                Log.Warn("AppraisalPalette: could not write defaults - " + ex.Message);
            }
        }

        private static void AppendMap(System.Text.StringBuilder sb, string name,
            Dictionary<string, Color> map, bool trailingComma)
        {
            sb.AppendFormat("  \"{0}\": {{\n", name);
            int i = 0;
            foreach (KeyValuePair<string, Color> kv in map)
            {
                sb.AppendFormat("    \"{0}\": \"{1}\"{2}\n", kv.Key, Hex(kv.Value),
                    ++i < map.Count ? "," : "");
            }
            sb.AppendFormat("  }}{0}\n", trailingComma ? "," : "");
        }
    }
}
