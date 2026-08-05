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
            // Exotic lifted off Destiny's verbatim #CEAE33. That value sits at hue 47.6,
            // right on the orange/gold boundary, and its low value (0.81) let it read as
            // muddy orange rather than gold. Pushed to hue 49.9 and value 0.95 — squarely
            // yellow-gold, and still 118 clear of its nearest neighbour.
            new Color(0xF2, 0xD6, 0x4B),   // Exotic    — yellow gold
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
            ["lightning"] = new Color(0xFF, 0xE1, 0x3B),
            ["fireball"] = new Color(0xF2, 0x3A, 0x1E),
            ["harm"] = new Color(0xD6, 0x24, 0x7A),
            ["magic arrow"] = new Color(0xC8, 0xA0, 0x60),
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

        // ── Live values ──────────────────────────────────────────────────────────────
        private static Color[] _tiers = (Color[])DefaultTiers.Clone();
        private static Dictionary<string, Color> _slayers = new(DefaultSlayers);
        private static Dictionary<string, Color> _wands = new(DefaultWands, StringComparer.OrdinalIgnoreCase);
        private static Color _unidentified = DefaultUnidentified;
        private static Color _durabilityPip = DefaultDurabilityPip;
        private static Color _genericWand = DefaultGenericWand;
        private static Color _containerOutline = DefaultContainerOutline;

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

            _slayers = ReadMap(root, "slayers", DefaultSlayers, StringComparer.Ordinal);
            _wands = ReadMap(root, "wands", DefaultWands, StringComparer.OrdinalIgnoreCase);
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
