// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;

namespace ClassicUO.Game.Managers
{
    /// <summary>
    /// In Mani Ylem mod: a world overlay of the server's resource banks (8x8 ore and fish, 4x3 wood),
    /// drawn by <see cref="Scenes.GameScene"/> as fine lines on the block boundaries, plus a
    /// countdown at a block's centre until its bank is back. Scripts drive it
    /// (LegionAPI.ShowBlockGrid / SetBlockTimer); nothing is saved, so a client restart starts
    /// clean and the script that knows the clocks sets them again.
    /// </summary>
    internal sealed class BlockGridManager
    {
        public static BlockGridManager Instance { get; } = new BlockGridManager();

        internal readonly struct Timer
        {
            public readonly uint ReadyAt;
            public readonly ushort Hue;

            public Timer(uint readyAt, ushort hue)
            {
                ReadyAt = readyAt;
                Hue = hue;
            }
        }

        public bool Show { get; private set; }
        public ushort Hue { get; private set; } = 1153;
        public int Size { get; private set; } = 8;
        /// <summary>Block height in tiles; <see cref="Size"/> is its width (RunUO's lumber banks are 4x3).</summary>
        public int SizeY { get; private set; } = 8;
        public int Radius { get; private set; } = 24;
        public int Thickness { get; private set; } = 1;

        /// <summary>(map, block x, block y) → when the bank is back, in <see cref="Time.Ticks"/>.</summary>
        public readonly Dictionary<(int map, int bx, int by), Timer> Timers = new();

        private BlockGridManager() { }

        public void SetGrid(bool show, ushort hue, int size, int radius, int thickness, int sizeY = 0)
        {
            Show = show;
            Hue = hue;
            Size = size < 1 ? 8 : size;
            SizeY = sizeY < 1 ? Size : sizeY;
            Radius = radius < 1 ? 24 : radius;
            Thickness = thickness < 1 ? 1 : thickness;
        }

        public void SetTimer(int map, int bx, int by, double seconds, ushort hue)
        {
            if (seconds <= 0)
            {
                Timers.Remove((map, bx, by));
                return;
            }

            Timers[(map, bx, by)] = new Timer(unchecked(Time.Ticks + (uint)(seconds * 1000)), hue);
        }

        public void ClearTimers() => Timers.Clear();

        /// <summary>Milliseconds left on a timer; zero or less once the bank is back.</summary>
        public static int Left(in Timer t) => unchecked((int)(t.ReadyAt - Time.Ticks));
    }
}
