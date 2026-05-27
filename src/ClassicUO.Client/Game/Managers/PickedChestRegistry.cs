// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ClassicUO.Game.Managers
{
    // Reads the chest-pick log written by the blaster_chestmaster.py LegionScript at
    //   ~/.local/share/TazUO/Data/Client/chestmaster_chests.csv
    // The script appends `x,y,map,label,marker_type,color,icon_size` per chest it
    // successfully lockpicks. We mirror those positions into an in-memory set so
    // the in-world glow on unpicked lockables can suppress itself for chests we've
    // already done.
    //
    // Auto-reload: on each IsPicked() call, if more than RELOAD_INTERVAL_SEC has
    // passed since the last filesystem check AND the CSV's mtime has changed,
    // re-read. Lightweight enough to call from a per-frame render predicate.
    internal static class PickedChestRegistry
    {
        private const double RELOAD_INTERVAL_SEC = 5.0;

        private static readonly HashSet<(int x, int y, int map)> _picked = new();
        private static readonly HashSet<(int x, int y, int map)> _puzzle = new();
        private static DateTime _lastReloadCheck = DateTime.MinValue;
        private static DateTime _lastPickedMtime = DateTime.MinValue;
        private static DateTime _lastPuzzleMtime = DateTime.MinValue;

        // Ported verbatim from blaster_chestmaster.py NON_CHEST_GRAPHICS so the
        // two stay aligned. When the script's blacklist changes, port the diff
        // by hand — both lists are short.
        public static readonly HashSet<ushort> NonChestGraphics = new()
        {
            0x2006, // corpse
            0x0E75, // backpack (player carried)
            0x0E76, // generic bag
            0x0E77, // barrel
            0x0E79, // pouch
            0x0E7A, // picnic basket
            0x0E80, // box (generic)
            0x0E83, // money bag
            0x0E84, // pouch variant
            0x099B, // bottle of ale
            0x09B0, // cooking pot
            0x09B2, // cooking pot variant
            0x09BF, // full jar
            0x09C0, // empty jar
            0x09EE, // crops
            0x09F0, // decoration
            0x0FAE, // barrel (variant)
            0x1849, // barrel (variant)
            0x184A, // barrel (variant)
            0x1855, // barrel (variant)
            0x0EFA, // spellbook (Magery)
            0x2252, // paladin book (Chivalry)
            0x2253, // necromancer book
            0x238C, // bushido book
            0x23A0, // ninjitsu book
            0x2D50, // book of bushido
            0x2D51, // book of ninjitsu
            0x2D9D, // spell weaving book
            0x14F0, // runebook
            0x14F5, // runebook (variant)
            0x0FBE, // book
            0x0FBD, // book (variant)
        };

        // Lowercase substring matches on OPL tooltip name (first line). Mirrors the
        // blaster_chestmaster.py filter sets (ALWAYS_SKIP_NAME_KEYWORDS + NON_CHEST_NAME_KEYWORDS)
        // plus "metal chest" because bank decoration metal chests reuse the same item
        // graphics (0x0E40, 0x0E7C) as dungeon treasure chests — only the tooltip name
        // distinguishes them ("metal chest" = bank static, "a metal chest" / "a treasure
        // chest" / "crate" = dynamic, possibly pickable).
        public static readonly string[] NonChestNameKeywords = new[]
        {
            // "metal chest" is NOT in this list — bank-deco metal chests are filtered
            // by the count==0 check below (they always tooltip with "(0 items, ...)"),
            // while pickable metal chests have empty data and pass through.
            //
            // Furniture / non-pickable static containers go here. The count==0 check
            // catches most of these too, but the name filter is a robust fallback for
            // items whose tooltip might lack the "(0 items, 0 stones)" line.
            "chest of drawers",
            "armoire",
            "wardrobe",
            "ballot box",
            "spellbook",
            "runebook",
            "tome",
            "scrollbook",
            "book of ",
            "chess",
            "checker",
            "backgammon",
            "dart board",
            "game board",
            "barrel",
            "keg",
            "trash",
        };

        public static bool IsKnownNonChestName(string oplText)
        {
            if (string.IsNullOrEmpty(oplText)) return false;
            // Match against the whole OPL blob (Name + Data merged). On this shard the
            // Name field is often empty for statics — the actual item name lives in
            // the single-click label that lands in Data via the merge-tooltip mod.
            string lower = oplText.ToLowerInvariant();
            foreach (string kw in NonChestNameKeywords)
            {
                if (lower.IndexOf(kw, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        // Matches the tooltip "(N items, M stones)" line UO renders under a container
        // whose contents are knowable to the client (i.e., it has been opened). Ported
        // verbatim from blaster_chestmaster.py:_CONTENTS_LINE_RE. If present in the
        // tooltip, the container is no longer "needs picking".
        private static readonly Regex _contentsLineRe = new Regex(
            @"\(\s*(\d+)\s*items?\s*,\s*\d+\s*stones?\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool HasItemCount(string oplText)
        {
            if (string.IsNullOrEmpty(oplText)) return false;
            return _contentsLineRe.IsMatch(oplText);
        }

        // Returns the int count from "(N items, M stones)" if present, else null.
        // Mirrors blaster_chestmaster.py's tooltip_item_count() — used to skip
        // count==0 containers (bank deco, chest of drawers, already-looted) while
        // still glowing containers whose tooltip just shows the count of items
        // they currently hold (e.g. "(1 items, 2 stones)" — opened but full).
        public static int? TooltipItemCount(string oplText)
        {
            if (string.IsNullOrEmpty(oplText)) return null;
            Match m = _contentsLineRe.Match(oplText);
            if (!m.Success) return null;
            if (int.TryParse(m.Groups[1].Value, out int count)) return count;
            return null;
        }

        private static string PickedCsvPath =>
            Path.Combine(CUOEnviroment.ExecutablePath, "Data", "Client", "chestmaster_chests.csv");
        private static string PuzzleCsvPath =>
            Path.Combine(CUOEnviroment.ExecutablePath, "Data", "Client", "chestmaster_puzzle_chests.csv");

        public static bool IsPicked(int x, int y, int map)
        {
            MaybeReload();
            return _picked.Contains((x, y, map));
        }

        public static bool IsPuzzle(int x, int y, int map)
        {
            MaybeReload();
            return _puzzle.Contains((x, y, map));
        }

        // Persistent log of puzzle chests detected client-side. Called from
        // ForcedTooltipManager when a single-click reply contains the shard's
        // "cannot be picked by normal means" message — that means the player
        // (or the chestmaster script) just tried to lockpick this container
        // and the server told us it's a puzzle. Recording here makes the
        // glow predicate flip to magenta immediately and survives restarts.
        public static void RecordPuzzle(int x, int y, int map)
        {
            (int, int, int) key = (x, y, map);
            lock (_puzzle)
            {
                if (_puzzle.Contains(key)) return;
                _puzzle.Add(key);
            }
            try
            {
                string line = string.Format("{0},{1},{2},Puzzle Chest,dot,magenta,3\n",
                    x, y, map);
                File.AppendAllText(PuzzleCsvPath, line);
                _lastPuzzleMtime = File.GetLastWriteTimeUtc(PuzzleCsvPath);
            }
            catch (IOException)
            {
                // Best-effort persist; in-memory set still works for this session.
            }
        }

        // The shard's stock journal/single-click line for failed picks against
        // puzzle-locked containers. Matched case-insensitively on the substring so
        // small wording variations ("This lock cannot…" vs "That lock cannot…") all
        // resolve to "this is a puzzle chest".
        public static bool IsPuzzleLockFailMessage(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return text.IndexOf("cannot be picked by normal means",
                StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Reload one CSV into the given set. Returns the file's new mtime, or
        // DateTime.MinValue if missing/unreadable so the caller can fall back to
        // a fresh load next tick.
        private static DateTime LoadCsv(string path, HashSet<(int, int, int)> target)
        {
            try
            {
                target.Clear();
                if (!File.Exists(path)) return DateTime.MinValue;

                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] parts = line.Split(',');
                    if (parts.Length < 3) continue;
                    if (int.TryParse(parts[0], out int x)
                        && int.TryParse(parts[1], out int y)
                        && int.TryParse(parts[2], out int m))
                    {
                        target.Add((x, y, m));
                    }
                }
                return File.GetLastWriteTimeUtc(path);
            }
            catch (IOException)
            {
                // Concurrent write by the LegionScript — try again on next tick.
                return DateTime.MinValue;
            }
        }

        public static void Reload()
        {
            _lastPickedMtime = LoadCsv(PickedCsvPath, _picked);
            _lastPuzzleMtime = LoadCsv(PuzzleCsvPath, _puzzle);
        }

        private static void MaybeReload()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastReloadCheck).TotalSeconds < RELOAD_INTERVAL_SEC) return;
            _lastReloadCheck = now;

            try
            {
                CheckOne(PickedCsvPath, _picked, ref _lastPickedMtime);
                CheckOne(PuzzleCsvPath, _puzzle, ref _lastPuzzleMtime);
            }
            catch (IOException)
            {
                // Filesystem hiccup — retry on next call.
            }
        }

        private static void CheckOne(string path, HashSet<(int, int, int)> target, ref DateTime lastMtime)
        {
            if (!File.Exists(path))
            {
                if (target.Count > 0) target.Clear();
                lastMtime = DateTime.MinValue;
                return;
            }

            DateTime mtime = File.GetLastWriteTimeUtc(path);
            if (mtime != lastMtime)
            {
                lastMtime = LoadCsv(path, target);
            }
        }
    }
}
