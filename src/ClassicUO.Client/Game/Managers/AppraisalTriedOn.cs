// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Managers
{
    /// <summary>
    /// What a piece gave when someone wore it, from the uo repo's appraise script:
    ///   LegionScripts/.appraise.txt   {"&lt;serial&gt;": {"gives": {"Int": 5}, "sure": true, ...}}
    ///
    /// Why this exists: magic clothing and antique jewelry on this shard keep a bare
    /// name and no properties even while worn, so the tooltip never says what one
    /// gives. The script finds out by trying each piece on and reading the stats; this
    /// hands its findings to the appraisal as the bonus lines the shard would have
    /// written ("Intelligence Bonus 5", "Peacemaking +5"), so labels, badges and
    /// colours come out exactly as for a piece whose tooltip names its bonus.
    ///
    /// Read-only, keyed by serial, hot-reloaded on mtime like the palette. A reading
    /// the script booked as unsure is left out: a wrong label is worse than none.
    /// </summary>
    internal static class AppraisalTriedOn
    {
        private const double RELOAD_INTERVAL_SEC = 2.0;

        private static DateTime _lastCheck = DateTime.MinValue;
        private static DateTime _lastMtime = DateTime.MinValue;
        private static Dictionary<uint, string[]> _lines = new();

        private static string FilePath =>
            Path.Combine(CUOEnviroment.ExecutablePath, "LegionScripts", ".appraise.txt");

        private static readonly Dictionary<string, string> Stats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Str"] = "Strength", ["Dex"] = "Dexterity", ["Int"] = "Intelligence",
        };

        // The script's resistance keys: real, but no bonus line on this shard names them.
        private static readonly HashSet<string> Resists = new(StringComparer.OrdinalIgnoreCase)
        {
            "Phys", "Fire", "Cold", "Pois", "Enrg",
        };

        /// <summary>The bonus lines for a piece someone tried on, or false.</summary>
        public static bool TryLines(uint serial, out string[] lines)
        {
            MaybeReload();
            return _lines.TryGetValue(serial, out lines);
        }

        private static void MaybeReload()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastCheck).TotalSeconds < RELOAD_INTERVAL_SEC)
                return;
            _lastCheck = now;

            try
            {
                if (!File.Exists(FilePath))
                {
                    _lines = new Dictionary<uint, string[]>();
                    _lastMtime = DateTime.MinValue;
                    return;
                }

                DateTime mtime = File.GetLastWriteTimeUtc(FilePath);
                if (mtime == _lastMtime)
                    return;

                _lines = Parse(File.ReadAllText(FilePath));
                _lastMtime = mtime;
            }
            catch (IOException)
            {
                // Mid-write: the script saves atomically, but try again next check anyway.
            }
            catch (JsonException e)
            {
                Log.Warn($"appraise file unreadable, keeping the last: {e.Message}");
                _lastMtime = File.GetLastWriteTimeUtc(FilePath);
            }
        }

        /// <summary>Serial → bonus lines, largest first; pieces that gave nothing are left out.</summary>
        internal static Dictionary<uint, string[]> Parse(string json)
        {
            var out_ = new Dictionary<uint, string[]>();
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return out_;

            foreach (JsonProperty piece in doc.RootElement.EnumerateObject())
            {
                if (!uint.TryParse(piece.Name, out uint serial) || piece.Value.ValueKind != JsonValueKind.Object)
                    continue;
                JsonElement rec = piece.Value;
                if (rec.TryGetProperty("sure", out JsonElement sure) && sure.ValueKind == JsonValueKind.False)
                    continue;
                if (!rec.TryGetProperty("gives", out JsonElement gives) || gives.ValueKind != JsonValueKind.Object)
                    continue;

                var found = new List<(int Value, string Line)>();
                foreach (JsonProperty g in gives.EnumerateObject())
                {
                    if (g.Value.ValueKind != JsonValueKind.Number || Resists.Contains(g.Name))
                        continue;
                    int value = (int)Math.Round(g.Value.GetDouble());
                    if (value <= 0)
                        continue;
                    found.Add((value, Stats.TryGetValue(g.Name, out string stat)
                        ? stat + " Bonus " + value
                        : g.Name + " +" + value));
                }
                if (found.Count > 0)
                    out_[serial] = found.OrderByDescending(f => f.Value).Select(f => f.Line).ToArray();
            }
            return out_;
        }
    }
}
