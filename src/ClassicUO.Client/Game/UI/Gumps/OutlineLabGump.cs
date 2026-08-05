// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.UI.Gumps
{
    /// <summary>
    /// Throwaway comparison board for outline legibility. Open with <c>-outlinelab</c>.
    ///
    /// The question it exists to answer: can a two-stop gradient outline carry two
    /// independent tiers (weapon damage and accuracy) legibly on real UO art? Pips can
    /// only live in a grid slot, so on the ground the outline is the sole channel — and
    /// if a gradient can hold two values there, the tier ramp doesn't have to choose
    /// between the two axes.
    ///
    /// It renders whatever is in your backpack rather than hardcoded graphics, so the
    /// verdict is about sprites you actually look at — dark leather, thin blades, and
    /// big plate all read very differently against an outline.
    ///
    /// Delete this file once the visual language is settled; nothing depends on it.
    /// </summary>
    internal class OutlineLabGump : Gump
    {
        private const int CELL = 62;      // swatch cell, comfortably over a 44px art tile
        private const int COLS = 6;
        private const int PAD = 10;
        private const int HEADER = 42;
        private const int CAPTION_H = 14;

        // Working ARPG ramp — grey/green/blue/purple/orange. These are the values the
        // real feature would use, so the board is testing the actual palette.
        private static readonly Color T1 = new Color(0x9A, 0x9A, 0x9A);   // Ruin / Defense
        private static readonly Color T2 = new Color(0x3F, 0xD1, 0x4E);   // Might / Guarding
        private static readonly Color T3 = new Color(0x35, 0x8C, 0xFF);   // Force / Hardening
        private static readonly Color T4 = new Color(0xA9, 0x4C, 0xE8);   // Power / Fortification
        private static readonly Color T5 = new Color(0xFF, 0xB0, 0x14);   // Vanquishing / Invuln.

        private readonly List<ushort> _graphics = new();

        // (label, top color, bottom color). A null end color means a flat outline —
        // the control group. Ordered so each gradient sits next to the two flat
        // outlines it is built from, which is the only fair way to judge it.
        private static readonly (string Label, Color Start, Color? End)[] SWATCHES =
        {
            ("T1 flat",   T1, null),
            ("T3 flat",   T3, null),
            ("T5 flat",   T5, null),
            ("T1>T5",     T1, T5),
            ("T5>T1",     T5, T1),
            ("T3>T5",     T3, T5),

            ("T2 flat",   T2, null),
            ("T4 flat",   T4, null),
            ("T2>T4",     T2, T4),
            ("T4>T2",     T4, T2),
            ("T5>T3",     T5, T3),
            ("T1>T3",     T1, T3),
        };

        public OutlineLabGump(World world) : base(world, 0, 0)
        {
            CanMove = true;
            CanCloseWithEsc = true;
            CanCloseWithRightClick = true;
            AcceptMouseInput = true;
            AcceptKeyboardInput = false;

            CollectGraphics();

            int rows = (SWATCHES.Length + COLS - 1) / COLS;
            Width = PAD * 2 + COLS * CELL;
            Height = HEADER + PAD + rows * (CELL + CAPTION_H) + PAD;

            Add(new AlphaBlendControl(0.85f) { Width = Width, Height = Height });

            Add(new Label("Outline lab — gradient vs flat", true, 0xFFFF) { X = PAD, Y = 8 });
            Add(new Label(
                _graphics.Count > 0
                    ? "art: your backpack  |  right-click to close"
                    : "backpack empty — showing nothing; put items in your pack",
                true, 0x0481) { X = PAD, Y = 24 });

            for (int i = 0; i < SWATCHES.Length; i++)
            {
                int col = i % COLS;
                int row = i / COLS;
                Add(new Label(SWATCHES[i].Label, true, 0x0481)
                {
                    X = PAD + col * CELL + 2,
                    Y = HEADER + PAD + row * (CELL + CAPTION_H) + CELL,
                });
            }
        }

        /// <summary>
        /// Distinct graphics from the backpack, so each swatch can show a different
        /// sprite and the comparison isn't biased by one silhouette.
        /// </summary>
        private void CollectGraphics()
        {
            Item backpack = World.Player?.FindItemByLayer(Data.Layer.Backpack);
            if (backpack == null)
                return;

            var seen = new HashSet<ushort>();
            for (LinkedObject i = backpack.Items; i != null; i = i.Next)
            {
                var item = (Item)i;
                if (item.IsDestroyed)
                    continue;
                if (seen.Add(item.DisplayedGraphic))
                    _graphics.Add(item.DisplayedGraphic);
            }
        }

        public override bool Draw(UltimaBatcher2D batcher, int x, int y)
        {
            base.Draw(batcher, x, y);

            if (_graphics.Count == 0)
                return true;

            Vector3 artHue = ShaderHueTranslator.GetHueVector(0, false, 1f);

            for (int i = 0; i < SWATCHES.Length; i++)
            {
                (string _, Color start, Color? end) = SWATCHES[i];

                // Cycle through the available art so neighbouring swatches differ.
                ushort graphic = _graphics[i % _graphics.Count];
                ref readonly SpriteInfo art = ref Client.Game.UO.Arts.GetArt(graphic);
                if (art.Texture == null)
                    continue;

                int col = i % COLS;
                int row = i / COLS;
                int cx = x + PAD + col * CELL;
                int cy = y + HEADER + PAD + row * (CELL + CAPTION_H);

                // Centre the art in its cell — UO art tiles vary a lot in size.
                var pos = new Vector2(
                    cx + (CELL - art.UV.Width) / 2f,
                    cy + (CELL - art.UV.Height) / 2f
                );

                batcher.Draw(
                    art.Texture,
                    new Rectangle((int)pos.X, (int)pos.Y, art.UV.Width, art.UV.Height),
                    art.UV,
                    artHue
                );

                // Clip the outline quad to the sprite's REAL bounds rather than the art
                // tile. The gradient interpolates across the quad, and UO tiles carry a
                // lot of transparent margin — so quad-mapped, the visible sprite only ever
                // samples the middle of the ramp and never reaches either endpoint.
                // Measured on the first build: T3>T5 spanned (98,148,203)->(177,162,111)
                // instead of (53,140,255)->(255,176,20), roughly half the intended range,
                // and small sprites collapsed to near-flat.
                //
                // Clipping is safe for the halo: the shader samples neighbours in
                // full-texture UV space, so it still finds the transparent pixels just
                // outside the bounds and draws the outline there. Anywhere a gradient
                // gets used for real, it needs this same treatment.
                Rectangle bounds = Client.Game.UO.Arts.GetRealArtBounds(graphic);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    bounds = new Rectangle(0, 0, art.UV.Width, art.UV.Height);

                var outlineUV = new Rectangle(
                    art.UV.X + bounds.X, art.UV.Y + bounds.Y, bounds.Width, bounds.Height);
                var outlinePos = new Vector2(pos.X + bounds.X, pos.Y + bounds.Y);

                batcher.DrawOutlined(
                    art.Texture,
                    outlinePos,
                    outlineUV,
                    ShaderHueTranslator.GetOutlineHueVector(),
                    ToVec(start),
                    0f,
                    Vector2.Zero,
                    Vector2.One,
                    SpriteEffects.None,
                    0f,
                    end.HasValue ? ToVec(end.Value) : (Vector3?)null
                );
            }

            return true;
        }

        private static Vector3 ToVec(Color c) => new Vector3(c.R / 255f, c.G / 255f, c.B / 255f);
    }
}
