// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Managers
{
    /// <summary>
    /// Where the towns are, for the loose-item highlight: the guard zones the world map
    /// already draws (Data/Client, named by the palette's loose.zones). The polygons
    /// trace tile corners around the union of RunUO's end-exclusive rects, so a tile is
    /// in when its centre is. The file is re-read when it changes, as the map does.
    /// </summary>
    internal static class TownZones
    {
        private sealed class Zone
        {
            public int MinX, MinY, MaxX, MaxY;
            public int[] Xs, Ys;
        }

        /// <summary>How often the file's mtime is looked at.</summary>
        private const uint CHECK_MS = 2000;
        /// <summary>Tiles remembered; a full cache is dropped and refilled, not trimmed.</summary>
        private const int CACHE_MAX = 16384;

        private static Zone[] _zones = Array.Empty<Zone>();
        private static int _map = -1;
        private static string _path;
        private static DateTime _mtime;
        private static uint _nextCheck;
        private static readonly Dictionary<long, bool> _cache = new();

        /// <summary>True when the tile lies in a guard zone of this map.</summary>
        public static bool Contains(int map, int x, int y)
        {
            MaybeReload();
            if (map != _map || _zones.Length == 0)
                return false;

            long key = ((long)x << 32) | (uint)y;
            if (_cache.TryGetValue(key, out bool hit))
                return hit;

            hit = false;
            foreach (Zone z in _zones)
            {
                if (x < z.MinX || x >= z.MaxX || y < z.MinY || y >= z.MaxY)
                    continue;
                if (Inside(z.Xs, z.Ys, x + 0.5, y + 0.5))
                {
                    hit = true;
                    break;
                }
            }

            if (_cache.Count >= CACHE_MAX)
                _cache.Clear();
            _cache[key] = hit;
            return hit;
        }

        /// <summary>Even-odd ray cast.</summary>
        internal static bool Inside(int[] xs, int[] ys, double px, double py)
        {
            bool inside = false;
            for (int i = 0, j = xs.Length - 1; i < xs.Length; j = i++)
            {
                if ((ys[i] > py) != (ys[j] > py)
                    && px < (double)(xs[j] - xs[i]) * (py - ys[i]) / (ys[j] - ys[i]) + xs[i])
                    inside = !inside;
            }
            return inside;
        }

        private static void MaybeReload()
        {
            if (Time.Ticks < _nextCheck)
                return;
            _nextCheck = Time.Ticks + CHECK_MS;

            string path = Path.Combine(CUOEnviroment.ExecutablePath, "Data", "Client", AppraisalPalette.LooseZones);
            try
            {
                if (!File.Exists(path))
                {
                    Clear(path);
                    return;
                }
                DateTime mtime = File.GetLastWriteTimeUtc(path);
                if (path == _path && mtime == _mtime)
                    return;
                Load(path);
                _path = path;
                _mtime = mtime;
            }
            catch (Exception e)
            {
                Log.Warn($"town zones: {path}: {e.Message}");
                Clear(path);
            }
        }

        private static void Clear(string path)
        {
            _zones = Array.Empty<Zone>();
            _map = -1;
            _path = path;
            _mtime = default;
            _cache.Clear();
        }

        private static void Load(string path)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            int map = root.TryGetProperty("MapIndex", out JsonElement mi) && mi.TryGetInt32(out int m) ? m : 0;

            var zones = new List<Zone>();
            if (root.TryGetProperty("Zones", out JsonElement ze) && ze.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement z in ze.EnumerateArray())
                {
                    Zone zone = Parse(z);
                    if (zone != null) zones.Add(zone);
                }
            }

            _zones = zones.ToArray();
            _map = map;
            _cache.Clear();
        }

        internal static bool TryParsePolygon(JsonElement z, out int[] xs, out int[] ys)
        {
            xs = ys = null;
            if (!z.TryGetProperty("Polygon", out JsonElement pe) || pe.ValueKind != JsonValueKind.Array)
                return false;
            var lx = new List<int>();
            var ly = new List<int>();
            foreach (JsonElement p in pe.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 2) continue;
                if (!p[0].TryGetInt32(out int x) || !p[1].TryGetInt32(out int y)) continue;
                lx.Add(x);
                ly.Add(y);
            }
            if (lx.Count < 3) return false;
            xs = lx.ToArray();
            ys = ly.ToArray();
            return true;
        }

        private static Zone Parse(JsonElement z)
        {
            if (!TryParsePolygon(z, out int[] xs, out int[] ys))
                return null;
            var zone = new Zone { Xs = xs, Ys = ys, MinX = int.MaxValue, MinY = int.MaxValue, MaxX = int.MinValue, MaxY = int.MinValue };
            for (int i = 0; i < xs.Length; i++)
            {
                zone.MinX = Math.Min(zone.MinX, xs[i]);
                zone.MaxX = Math.Max(zone.MaxX, xs[i]);
                zone.MinY = Math.Min(zone.MinY, ys[i]);
                zone.MaxY = Math.Max(zone.MaxY, ys[i]);
            }
            return zone;
        }
    }
}
