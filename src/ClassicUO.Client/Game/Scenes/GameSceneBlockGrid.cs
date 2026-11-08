// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.Scenes
{
    public partial class GameScene
    {
        /// <summary>A block's countdown text, rebuilt only when its m:ss or hue changes.</summary>
        private readonly Dictionary<(int map, int bx, int by), (string text, ushort hue, RenderedText rt)> _blockLabels = new();
        private readonly List<(int map, int bx, int by)> _blockScratch = new();

        /// <summary>
        /// In Mani Ylem mod: the 8x8 bank grid and its countdowns (<see cref="BlockGridManager"/>).
        ///
        /// Drawn in the overlay pass, under the camera transform, after the lights: a line darkened
        /// with the night would be lost against rock. A boundary runs along tile corners, and each
        /// corner sits at the height the land is drawn at there (the stretched-land rule: a corner
        /// is the Z of the land cell it tops), so on a mountainside the line follows the slope.
        /// Where the land is not drawn at all (above the player's ceiling: the rock over a cave)
        /// the corner drops to the player's Z, which in a cave is the floor.
        /// </summary>
        private void DrawBlockGrid(UltimaBatcher2D batcher)
        {
            BlockGridManager grid = BlockGridManager.Instance;

            if ((!grid.Show && grid.Timers.Count == 0) || _world.Player == null || _world.Map == null)
            {
                return;
            }

            int size = grid.Size;
            int px = _world.Player.X;
            int py = _world.Player.Y;
            int x0 = px - grid.Radius;
            int x1 = px + grid.Radius;
            int y0 = py - grid.Radius;
            int y1 = py + grid.Radius;

            if (grid.Show)
            {
                Texture2D white = SolidColorTextureCache.GetTexture(Color.White);
                Vector3 hue = ShaderHueTranslator.GetHueVector(grid.Hue, false, 0.85f);
                float stroke = grid.Thickness;

                // Lines of constant x (between tiles x-1 and x), each a run of corner-to-corner
                // segments down the y axis; then the same across.
                for (int x = FirstLine(x0, size); x <= x1 + 1; x += size)
                {
                    Vector2 a = CornerToScreen(x, y0);

                    for (int y = y0 + 1; y <= y1 + 1; y++)
                    {
                        Vector2 b = CornerToScreen(x, y);
                        batcher.DrawLine(white, a, b, hue, stroke, 0f);
                        a = b;
                    }
                }

                for (int y = FirstLine(y0, size); y <= y1 + 1; y += size)
                {
                    Vector2 a = CornerToScreen(x0, y);

                    for (int x = x0 + 1; x <= x1 + 1; x++)
                    {
                        Vector2 b = CornerToScreen(x, y);
                        batcher.DrawLine(white, a, b, hue, stroke, 0f);
                        a = b;
                    }
                }
            }

            DrawBlockTimers(batcher, grid, x0, x1, y0, y1);
        }

        private void DrawBlockTimers(UltimaBatcher2D batcher, BlockGridManager grid, int x0, int x1, int y0, int y1)
        {
            int map = _world.Map.Index;
            int size = grid.Size;

            _blockScratch.Clear();

            foreach (KeyValuePair<(int map, int bx, int by), BlockGridManager.Timer> kv in grid.Timers)
            {
                int left = BlockGridManager.Left(kv.Value);

                if (left <= 0)
                {
                    _blockScratch.Add(kv.Key);
                    continue;
                }

                (int m, int bx, int by) = kv.Key;
                int cx = bx * size + size / 2;
                int cy = by * size + size / 2;

                if (m != map || cx < x0 || cx > x1 || cy < y0 || cy > y1)
                {
                    continue;
                }

                string text = Countdown(left);
                ushort hue = kv.Value.Hue != 0 ? kv.Value.Hue : grid.Hue;

                if (!_blockLabels.TryGetValue(kv.Key, out var label) || label.text != text || label.hue != hue)
                {
                    label.rt?.Destroy();
                    label = (text, hue, RenderedText.Create(text, hue, 0xFF, true, FontStyle.BlackBorder));
                    _blockLabels[kv.Key] = label;
                }

                Vector2 p = CornerToScreen(cx, cy);
                label.rt.Draw(batcher, (int)p.X - (label.rt.Width >> 1), (int)p.Y + 22 - (label.rt.Height >> 1));
            }

            // A bank that is back stops counting; its label goes with it.
            foreach ((int map, int bx, int by) key in _blockScratch)
            {
                grid.Timers.Remove(key);
            }

            if (_blockLabels.Count > grid.Timers.Count)
            {
                _blockScratch.Clear();

                foreach ((int map, int bx, int by) key in _blockLabels.Keys)
                {
                    if (!grid.Timers.ContainsKey(key))
                    {
                        _blockScratch.Add(key);
                    }
                }

                foreach ((int map, int bx, int by) key in _blockScratch)
                {
                    _blockLabels[key].rt?.Destroy();
                    _blockLabels.Remove(key);
                }
            }
        }

        private static int FirstLine(int from, int size)
        {
            int r = from % size;

            return r == 0 ? from : from + (size - (r < 0 ? r + size : r));
        }

        /// <summary>Rounded up, so a countdown never reads 0:00 while the bank is still out.</summary>
        private static string Countdown(int ms)
        {
            int s = (ms + 999) / 1000;

            return s >= 3600
                ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}"
                : $"{s / 60}:{s % 60:00}";
        }

        /// <summary>
        /// The top vertex of tile (cx, cy) in the space objects' RealScreenPosition is in (see
        /// GameObject.UpdateRealScreenPosition and Batcher2D.DrawStretchedLand).
        /// </summary>
        private Vector2 CornerToScreen(int cx, int cy)
        {
            sbyte z = _world.Map.GetTileZ(cx, cy);

            if (z == -125 || z > _maxGroundZ)
            {
                z = _world.Player.Z;
            }

            return new Vector2(
                (cx - cy) * 22 - _offset.X,
                (cx + cy) * 22 - (z << 2) - _offset.Y - 22
            );
        }
    }
}
