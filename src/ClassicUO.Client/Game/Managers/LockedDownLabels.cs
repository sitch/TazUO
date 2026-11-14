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
    ///
    /// Only an answer decides. <see cref="Ask"/> clicks an item, <see cref="OnLabel"/>
    /// collects every line the shard sends back for it (a hover's answer counts too),
    /// and once the burst has settled the item is locked exactly when a line said so.
    /// The OPL text is not trusted while an answer can still come: a rebroadcast keeps
    /// a stale "locked down" name or lacks a fresh one, and an item would flash loose
    /// before its first answer. A house item is asked again every
    /// <see cref="RECHECK_MS"/>, because a released plant keeps its flag; an item the
    /// server makes not-movable is forgotten (<see cref="NotMovable"/>), so when it is
    /// released and the flag comes back it is asked at once.
    ///
    /// The shard says "locked down" only to someone standing in that house: RunUO
    /// asks the clicker's own region (<c>from.Region.OnSingleClick</c>, and
    /// HouseRegion adds the label), so from the street every plant answers bare
    /// (journal 2026-11-14, Walter outside a neighbour's house). An answer with no
    /// "locked down" decides only when <c>trusted</c> (the asker inside the item's
    /// house); otherwise the item keeps what it was and isn't asked.
    /// </summary>
    internal static class LockedDownLabels
    {
        internal enum State { Unknown, Loose, LockedDown }

        /// <summary>How long a click's answer may take; the label window is 500 ms.</summary>
        private const uint ANSWER_MS = 1500;
        /// <summary>Quiet time after an answer's first line before it decides; a burst spans tens of ms.</summary>
        private const uint SETTLE_MS = 300;
        /// <summary>Gap before clicking again after no answer.</summary>
        private const uint ASK_EVERY_MS = 2000;
        /// <summary>Unanswered clicks before the OPL text alone decides.</summary>
        private const int ASK_TRIES = 3;
        /// <summary>
        /// A house item is asked again this often: how long a released plant or
        /// statue (whose flag doesn't change) or a plant just locked down waits before
        /// its look changes. The budget stretches it in a house with more items than
        /// it can ask in the time.
        /// </summary>
        private const uint RECHECK_MS = 4000;
        /// <summary>Clicks sent per second, all items together; a house can hold hundreds.</summary>
        private const int ASKS_PER_SECOND = 10;
        /// <summary>Of those, at most this many go to rechecks, so a newly placed item never queues behind them.</summary>
        private const int RECHECKS_PER_SECOND = 5;
        private const int CACHE_MAX = 8192;

        private sealed class Entry
        {
            public State State;
            /// <summary>Tick the outstanding click went out, 0 for none.</summary>
            public uint Sent;
            /// <summary>Tick the first line of its answer arrived, 0 for none yet.</summary>
            public uint Answered;
            public bool SawLocked;
            public int Tries;
            public uint Next;
            /// <summary>Asked as a movable item (the loose check), not a fixed landmark.</summary>
            public bool Movable;
            /// <summary>A bare answer would mean loose: the asker stands where the shard says "locked down".</summary>
            public bool Trusted = true;
        }

        private static readonly Dictionary<uint, Entry> _entries = new();
        private static uint _window;
        private static int _windowAsks;
        private static int _windowRechecks;

        /// <summary>What is known, without asking: the last answer, else the OPL text.</summary>
        public static State Read(World world, uint serial)
        {
            if (_entries.TryGetValue(serial, out Entry e) && e.State != State.Unknown)
                return e.State;
            if (!world.OPL.TryGetNameAndData(serial, out string name, out string data))
                return State.Unknown;
            return Says(name) || Says(data) ? State.LockedDown : State.Loose;
        }

        /// <summary>True when the item is known to be locked down.</summary>
        public static bool IsLockedDown(World world, uint serial) => Read(world, serial) == State.LockedDown;

        /// <summary>
        /// The item's state, clicking it when an answer is due: never answered yet, or
        /// (<paramref name="recheck"/>) its last answer is <see cref="RECHECK_MS"/> old.
        /// Unknown until the first answer settles. Not <paramref name="trusted"/>, a
        /// bare answer decides nothing and no click goes out (it could only say what
        /// the dark item already shows).
        /// </summary>
        public static State Ask(World world, uint serial, bool recheck, bool movable = true, bool trusted = true)
        {
            if (!_entries.TryGetValue(serial, out Entry e))
            {
                if (_entries.Count >= CACHE_MAX)
                    _entries.Clear();
                e = new Entry();
                _entries[serial] = e;
            }
            e.Movable = movable;
            e.Trusted = trusted;

            uint now = Time.Ticks;
            if (e.Sent != 0)
            {
                if (e.Answered != 0)
                {
                    if (now - e.Answered < SETTLE_MS)
                        return e.State;
                    if (e.SawLocked || e.Trusted)
                        e.State = e.SawLocked ? State.LockedDown : State.Loose;
                    e.Sent = 0;
                    e.Tries = 0;
                    e.Next = now + RECHECK_MS;
                    if (!e.SawLocked && e.Trusted)
                        world.OPL.StripLines(serial, Says);   // a stale "locked down" name or line
                    return e.State;
                }
                if (now - e.Sent < ANSWER_MS)
                    return e.State;
                e.Sent = 0;
                e.Tries++;
                e.Next = now + ASK_EVERY_MS;
                if (e.State == State.Unknown && e.Tries >= ASK_TRIES)
                {
                    // The shard never answered: the OPL text, or else the flag, decides.
                    bool says = world.OPL.TryGetNameAndData(serial, out string name, out string data)
                                && (Says(name) || Says(data));
                    if (says || e.Trusted)
                        e.State = says ? State.LockedDown : State.Loose;
                    e.Tries = 0;
                    e.Next = now + RECHECK_MS;
                }
                return e.State;
            }

            bool due = trusted && (e.State == State.Unknown || recheck);
            if (!due || now < e.Next || !Spend(e.State != State.Unknown))
                return e.State;

            e.Sent = now;
            e.Answered = 0;
            e.SawLocked = false;
            ForcedTooltipManager.RequestName(world, serial);
            return e.State;
        }

        /// <summary>
        /// A label line the shard sent for an item within its click window, from
        /// <see cref="ForcedTooltipManager.IsObjectTextRequested"/>. A line nobody here
        /// asked for (a hover's) opens an answer of its own.
        /// </summary>
        public static void OnLabel(uint serial, string text)
        {
            if (!_entries.TryGetValue(serial, out Entry e))
                return;
            uint now = Time.Ticks;
            if (e.Sent == 0 || now - e.Sent >= ANSWER_MS)
            {
                e.Sent = now;
                e.Answered = 0;
                e.SawLocked = false;
            }
            if (e.Answered == 0)
                e.Answered = now;
            if (Says(text))
                e.SawLocked = true;
        }

        /// <summary>
        /// The server sent the item not-movable: drop what the loose check knew of it,
        /// so when it is released and reads movable again it is asked at once. A fixed
        /// landmark's own answer (asked with movable false) is kept.
        /// </summary>
        public static void NotMovable(uint serial)
        {
            if (_entries.TryGetValue(serial, out Entry e) && e.Movable)
                _entries.Remove(serial);
        }

        private static bool Spend(bool isRecheck)
        {
            if (Time.Ticks - _window >= 1000)
            {
                _window = Time.Ticks;
                _windowAsks = 0;
                _windowRechecks = 0;
            }
            if (_windowAsks >= ASKS_PER_SECOND || (isRecheck && _windowRechecks >= RECHECKS_PER_SECOND))
                return false;
            _windowAsks++;
            if (isRecheck) _windowRechecks++;
            return true;
        }

        /// <summary>A label line saying the item is locked down (or locked down and secure).</summary>
        internal static bool Says(string text) =>
            !string.IsNullOrEmpty(text) && text.IndexOf("locked down", StringComparison.OrdinalIgnoreCase) >= 0;

        public static void Clear() => _entries.Clear();
    }
}
