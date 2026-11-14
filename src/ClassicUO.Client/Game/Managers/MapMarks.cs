// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Managers
{
    /// <summary>
    /// World map markers the client writes itself, one CSV file per kind (houses,
    /// landmarks) beside the other marker files in Data/Client: one line a place,
    /// <c>x,y,map,name,dot,colour,3</c>, the shape the world map reads. A new place is
    /// appended and a renamed one rewritten, both in place, so a symlinked file stays
    /// a link; the marker is also put on a world map already loaded, so it shows
    /// without a reload.
    /// </summary>
    internal static class MapMarks
    {
        internal enum Outcome { Known, New, Renamed }

        private sealed class Mark
        {
            public int X, Y, Map;
            public string Name;
        }

        private static readonly Dictionary<string, Dictionary<(int, int, int), Mark>> _files = new(StringComparer.OrdinalIgnoreCase);

        private static string PathOf(string file) => Path.Combine(CUOEnviroment.ExecutablePath, "Data", "Client", file);

        /// <summary>
        /// Put <paramref name="name"/> at x,y on <paramref name="map"/> in
        /// <paramref name="file"/>. <paramref name="oldName"/> is the name it had when
        /// renamed.
        /// </summary>
        public static Outcome Note(string file, int x, int y, int map, string name, string color, out string oldName)
        {
            oldName = null;
            name = (name ?? string.Empty).Replace(',', ' ').Trim();   // the CSV has no quoting
            if (name.Length == 0)
                return Outcome.Known;
            Dictionary<(int, int, int), Mark> marks = Load(file);
            var key = (x, y, map);
            if (marks.TryGetValue(key, out Mark known))
            {
                if (known.Name == name)
                    return Outcome.Known;
                oldName = known.Name;
                known.Name = name;
                Save(file, marks, color);
                Show(file, x, y, map, name, color);
                return Outcome.Renamed;
            }

            var mark = new Mark { X = x, Y = y, Map = map, Name = name };
            marks[key] = mark;
            try
            {
                File.AppendAllText(PathOf(file), Line(mark, color) + "\n");
            }
            catch (Exception e)
            {
                Log.Warn($"MapMarks: can't append to {file}: {e.Message}");
            }
            Show(file, x, y, map, name, color);
            return Outcome.New;
        }

        /// <summary>Whether the file already holds a mark at x,y on the map.</summary>
        public static bool Has(string file, int x, int y, int map) => Load(file).ContainsKey((x, y, map));

        private static string Line(Mark m, string color) => $"{m.X},{m.Y},{m.Map},{m.Name},dot,{color},3";

        private static Dictionary<(int, int, int), Mark> Load(string file)
        {
            if (_files.TryGetValue(file, out Dictionary<(int, int, int), Mark> marks))
                return marks;
            marks = new Dictionary<(int, int, int), Mark>();
            _files[file] = marks;
            try
            {
                string path = PathOf(file);
                if (!File.Exists(path))
                    return marks;
                foreach (string line in File.ReadAllLines(path))
                {
                    string[] f = line.Split(',');
                    if (f.Length < 4 || !int.TryParse(f[0], out int x) || !int.TryParse(f[1], out int y)
                        || !int.TryParse(f[2], out int map))
                        continue;
                    marks[(x, y, map)] = new Mark { X = x, Y = y, Map = map, Name = f[3] };
                }
            }
            catch (Exception e)
            {
                Log.Warn($"MapMarks: can't read {file}: {e.Message}");
            }
            return marks;
        }

        private static void Save(string file, Dictionary<(int, int, int), Mark> marks, string color)
        {
            try
            {
                var lines = new List<string>();
                foreach (Mark m in marks.Values)
                    lines.Add(Line(m, color));
                lines.Sort(StringComparer.Ordinal);
                File.WriteAllText(PathOf(file), string.Join("\n", lines) + "\n");
            }
            catch (Exception e)
            {
                Log.Warn($"MapMarks: can't write {file}: {e.Message}");
            }
        }

        private static void Show(string file, int x, int y, int map, string name, string color)
        {
            List<WorldMapGump.WMapMarkerFile> files = WorldMapGump._markerFiles;
            if (files.Count == 0)
                return;   // markers not loaded yet: the file is read when they are
            WorldMapGump.WMapMarkerFile found = null;
            foreach (WorldMapGump.WMapMarkerFile f in files)
                if (string.Equals(Path.GetFileName(f.FullPath), file, StringComparison.OrdinalIgnoreCase))
                    found = f;
            if (found == null)
            {
                found = new WorldMapGump.WMapMarkerFile
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    FullPath = PathOf(file),
                    Markers = new List<WorldMapGump.WMapMarker>(),
                    IsEditable = true,
                };
                files.Add(found);
            }
            WorldMapGump.WMapMarker marker = found.Markers.Find(m => m.X == x && m.Y == y && m.MapId == map);
            if (marker == null)
            {
                marker = new WorldMapGump.WMapMarker
                {
                    X = x, Y = y, MapId = map,
                    MarkerIconName = "dot",
                    Color = WorldMapGump.GetColor(color),
                    ZoomIndex = 3,
                };
                found.Markers.Add(marker);
            }
            marker.Name = name;
        }
    }
}
