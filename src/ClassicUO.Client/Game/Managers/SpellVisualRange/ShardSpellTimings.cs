using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ClassicUO.Configuration;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Managers.SpellVisualRange
{
    internal static class ShardSpellTimings
    {
        private const double FALLBACK_CIRCLE1_CAST_SEC = 0.65;
        private const double CAST_INCREMENT_PER_CIRCLE_SEC = 0.25;
        private const double FALLBACK_RECOVERY_SEC = 0.7;

        public static void LoadAndApply(Dictionary<int, SpellRangeInfo> cache)
        {
            if (ProfileManager.CurrentProfile?.UseShardSpellTimings != true)
                return;

            string path = Path.Combine(CUOEnviroment.ExecutablePath ?? string.Empty, "Data", "Client", "in_mani_ylem_server_profile.json");
            if (!File.Exists(path))
                return;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement root = doc.RootElement;

                string shard = TryGetString(root, "shard") ?? "unknown shard";
                string calibratedAt = TryGetString(root, "calibrated_at") ?? "unknown date";

                Dictionary<string, (double? avg, int samples)> castBySpell = new(StringComparer.Ordinal);
                Dictionary<string, double?> recoveryBySpell = new(StringComparer.Ordinal);
                Dictionary<int, double?> recoveryByCircle = new();

                if (root.TryGetProperty("spell_cast", out JsonElement castEl) &&
                    castEl.TryGetProperty("by_spell", out JsonElement castBy))
                {
                    foreach (JsonProperty p in castBy.EnumerateObject())
                    {
                        double? avg = TryGetDouble(p.Value, "avg");
                        int samples = TryGetInt(p.Value, "samples") ?? 0;
                        castBySpell[NormName(p.Name)] = (avg, samples);
                    }
                }

                if (root.TryGetProperty("spell_recovery", out JsonElement recEl))
                {
                    if (recEl.TryGetProperty("by_spell", out JsonElement recBy))
                    {
                        foreach (JsonProperty p in recBy.EnumerateObject())
                            recoveryBySpell[NormName(p.Name)] = TryGetDouble(p.Value, "min_stable_pause_sec");
                    }

                    if (recEl.TryGetProperty("by_circle", out JsonElement recCircleEl))
                    {
                        foreach (JsonProperty p in recCircleEl.EnumerateObject())
                        {
                            if (TryParseCircleKey(p.Name, out int circle))
                                recoveryByCircle[circle] = TryGetDouble(p.Value, "max_recovery_sec");
                        }
                    }
                }

                int overrideCount = 0;
                int extrapolatedCount = 0;

                foreach (SpellRangeInfo entry in cache.Values)
                {
                    if (entry.School != "Magery")
                        continue;

                    int circle = MageryCircleFromId(entry.ID);
                    if (circle < 1 || circle > 8)
                        continue;

                    string key = NormName(entry.Name);

                    if (castBySpell.TryGetValue(key, out (double? avg, int samples) sample) && sample.samples > 0 && sample.avg.HasValue)
                    {
                        entry.CastTime = sample.avg.Value;
                    }
                    else
                    {
                        entry.CastTime = FALLBACK_CIRCLE1_CAST_SEC + CAST_INCREMENT_PER_CIRCLE_SEC * (circle - 1);
                        extrapolatedCount++;
                    }

                    if (recoveryBySpell.TryGetValue(key, out double? spellMin) && spellMin.HasValue)
                        entry.RecoveryTime = spellMin.Value;
                    else if (recoveryByCircle.TryGetValue(circle, out double? circMax) && circMax.HasValue)
                        entry.RecoveryTime = circMax.Value;
                    else
                        entry.RecoveryTime = FALLBACK_RECOVERY_SEC;

                    overrideCount++;
                }

                Log.Info($"[ShardTimings] Loaded {shard} profile (calibrated {calibratedAt}), overrode CastTime/RecoveryTime on {overrideCount} Magery spells ({extrapolatedCount} extrapolated).");
            }
            catch (Exception ex)
            {
                Log.Error($"[ShardTimings] Failed to apply shard profile: {ex}");
            }
        }

        private static string NormName(string s) => (s ?? string.Empty).Replace(" ", string.Empty).ToLowerInvariant();

        private static int MageryCircleFromId(int id) => id >= 1 && id <= 64 ? ((id - 1) / 8) + 1 : -1;

        private static bool TryParseCircleKey(string name, out int circle)
        {
            circle = 0;
            const string prefix = "circle_";
            return name != null && name.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(name.Substring(prefix.Length), out circle);
        }

        private static string TryGetString(JsonElement el, string prop) =>
            el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static double? TryGetDouble(JsonElement el, string prop) =>
            el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

        private static int? TryGetInt(JsonElement el, string prop) =>
            el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    }
}
