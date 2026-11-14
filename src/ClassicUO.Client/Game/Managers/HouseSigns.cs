// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.Game.Managers
{
    /// <summary>
    /// House signs: never outlined, and every house met is put on the world map. A
    /// sign reads movable (RunUO forces its properties to show, which sets the same
    /// flag) and answers a click with no "locked down", so the loose check took it
    /// for a thing anyone could carry off. The shard's answer gives it away: "This
    /// house is like new." first, then the house's name (journal 2026-11-12/13).
    ///
    /// A house the marker file (<see cref="FILE"/>, through <see cref="MapMarks"/>)
    /// doesn't know yet gets a marker at its sign and a soft journal line with a harp
    /// note; a renamed one has its marker renamed.
    /// </summary>
    internal static class HouseSigns
    {
        public const string FILE = "in_mani_ylem_houses.csv";
        /// <summary>Marker colour on the world map: a calm green no shop or rare uses.</summary>
        private const string MARKER_COLOR = "#7FD1A8";
        /// <summary>Journal hue: the scripts' INFO blue, gentler than a grade's or the house warning's.</summary>
        private const ushort HUE = 88;
        /// <summary>A harp played well: a friendly note, not the rares' chime. 0 for silence.</summary>
        private const ushort SOUND = 0x45;
        /// <summary>One note at most this often, so riding through a housing area gives one.</summary>
        private const uint SOUND_GAP_MS = 4000;

        private static readonly HashSet<uint> _signs = new();
        private static uint _lastSound;

        /// <summary>
        /// Sign art: a tile named a sign, or anything in the range a house sign can be
        /// changed to (RunUO's shop signs, "Tinker", "Bakery", whose tile names don't
        /// say sign). Its look waits for the shard's answer.
        /// </summary>
        public static bool IsSignArt(Item item)
        {
            if (item.Graphic >= 0x0B95 && item.Graphic <= 0x0C0E)
                return true;
            string art = item.ItemData.Name;
            return !string.IsNullOrEmpty(art) && art.IndexOf("sign", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// True when the item is a house sign, by its answer ("This house is ..."),
        /// whatever its art. Sign art that hasn't answered yet is asked (a not-movable
        /// sign has nothing else asking for it); once the name has come too, the house
        /// is noted.
        /// </summary>
        public static bool Check(World world, Item item)
        {
            if (!item.OnGround)
                return false;
            if (!world.OPL.TryGetNameAndData(item.Serial, out string first, out string data))
            {
                if (!item.IsMovable && IsSignArt(item))
                    LockedDownLabels.Ask(world, item.Serial, recheck: false, movable: false);
                return false;
            }
            if (!SaysHouse(first) && (string.IsNullOrEmpty(data) || data.IndexOf(HOUSE_LINE, StringComparison.OrdinalIgnoreCase) < 0))
                return false;
            _signs.Add(item.Serial);
            string name = NameOf(first, data);
            if (name != null)
                Note(world, item.X, item.Y, world.MapIndex, name);
            return true;
        }

        /// <summary>Known to be a house sign this session.</summary>
        public static bool IsKnownSign(uint serial) => _signs.Contains(serial);

        private const string HOUSE_LINE = "This house is";

        private static bool SaysHouse(string line) =>
            line != null && line.TrimStart().StartsWith(HOUSE_LINE, StringComparison.OrdinalIgnoreCase);

        /// <summary>The house's name: the first line that is neither the condition, "locked down" nor property text.</summary>
        private static string NameOf(string first, string data)
        {
            var lines = new List<string> { first };
            if (!string.IsNullOrEmpty(data))
                lines.AddRange(data.Split('\n'));
            foreach (string raw in lines)
            {
                string line = raw?.Trim();
                if (string.IsNullOrEmpty(line) || SaysHouse(line))
                    continue;
                if (!LockedDownLabels.Says(line) && !ForcedTooltipManager.LooksLikePropertyText(line))
                    return line;
            }
            return null;
        }

        private static void Note(World world, int x, int y, int map, string name)
        {
            switch (MapMarks.Note(FILE, x, y, map, name, MARKER_COLOR, out string old))
            {
                case MapMarks.Outcome.New:
                    if (SOUND != 0 && Time.Ticks - _lastSound >= SOUND_GAP_MS)
                    {
                        _lastSound = Time.Ticks;
                        Client.Game.Audio?.PlaySound(SOUND);
                    }
                    GameActions.Print(world, "\u203B A new house \u00B7 " + name + " \u00B7 now on your map", HUE);
                    break;
                case MapMarks.Outcome.Renamed:
                    GameActions.Print(world, "\u203B A house renamed \u00B7 " + old + " \u2192 " + name, HUE);
                    break;
            }
        }

        public static void Clear() => _signs.Clear();
    }
}
