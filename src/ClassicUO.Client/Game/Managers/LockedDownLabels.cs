// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;

namespace ClassicUO.Game.Managers
{
    /// <summary>
    /// Whether a ground item is locked down, from the shard's own click label. The
    /// Movable flag can't say: RunUO sets 0x20 for an item that forces its properties
    /// to show (plants, statues) whether or not it is locked down, so a house's potted
    /// plants and statues read movable. A click on a locked-down item answers "locked
    /// down" first, then the name (journal 2026-11-11: chests, bookcases, backpacks).
    /// The label channel only fills once something asks, so <see cref="Ask"/> sends one
    /// click per item, a few times at most, under a global budget.
    /// </summary>
    internal static class LockedDownLabels
    {
        internal enum State { Unknown, Loose, LockedDown }

        /// <summary>Gap between asks for one item; past the merger's 500 ms reply window.</summary>
        private const uint ASK_EVERY_MS = 2000;
        /// <summary>Unanswered asks before the flag alone decides.</summary>
        private const int ASK_TRIES = 3;
        /// <summary>Clicks sent per second, all items together; a house can hold hundreds.</summary>
        private const int ASKS_PER_SECOND = 10;
        private const int CACHE_MAX = 8192;

        private struct Seen
        {
            public uint Revision;
            public bool Locked;
        }

        private struct Asked
        {
            public uint Next;
            public int Tries;
        }

        private static readonly Dictionary<uint, Seen> _seen = new();
        private static readonly Dictionary<uint, Asked> _asked = new();
        private static uint _window;
        private static int _windowAsks;

        /// <summary>What the label already says, without asking.</summary>
        public static State Read(World world, uint serial)
        {
            if (!world.OPL.TryGetRevision(serial, out uint rev))
                return State.Unknown;
            if (_seen.TryGetValue(serial, out Seen s) && (s.Revision == rev || s.Locked))
                return s.Locked ? State.LockedDown : State.Loose;   // locked is sticky: a rebroadcast can drop the label
            if (!world.OPL.TryGetNameAndData(serial, out string name, out string data))
                return State.Unknown;

            bool locked = Says(name) || Says(data);
            if (_seen.Count >= CACHE_MAX)
                _seen.Clear();
            _seen[serial] = new Seen { Revision = rev, Locked = locked };
            return locked ? State.LockedDown : State.Loose;
        }

        /// <summary>True when the label is known to say locked down.</summary>
        public static bool IsLockedDown(World world, uint serial) => Read(world, serial) == State.LockedDown;

        /// <summary>
        /// <see cref="Read"/>, asking for the label when nothing is known. Unknown while
        /// the asks are out; Loose once they all went unanswered, so the flag stands.
        /// </summary>
        public static State Ask(World world, uint serial)
        {
            State state = Read(world, serial);
            if (state != State.Unknown)
                return state;

            _asked.TryGetValue(serial, out Asked a);
            if (a.Tries >= ASK_TRIES)
                return Time.Ticks >= a.Next ? State.Loose : State.Unknown;
            if (Time.Ticks < a.Next || !Spend())
                return State.Unknown;

            a.Tries++;
            a.Next = Time.Ticks + ASK_EVERY_MS;
            if (_asked.Count >= CACHE_MAX)
                _asked.Clear();
            _asked[serial] = a;
            ForcedTooltipManager.RequestName(world, serial);
            return State.Unknown;
        }

        private static bool Spend()
        {
            if (Time.Ticks - _window >= 1000)
            {
                _window = Time.Ticks;
                _windowAsks = 0;
            }
            if (_windowAsks >= ASKS_PER_SECOND)
                return false;
            _windowAsks++;
            return true;
        }

        /// <summary>A label line saying the item is locked down (or locked down and secure).</summary>
        internal static bool Says(string text) =>
            !string.IsNullOrEmpty(text) && text.IndexOf("locked down", StringComparison.OrdinalIgnoreCase) >= 0;

        public static void Clear()
        {
            _seen.Clear();
            _asked.Clear();
        }
    }
}
