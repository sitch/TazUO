// SPDX-License-Identifier: BSD-2-Clause

using System;
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
        /// <summary>One outlined item drawn this frame, kept for the x-ray and label passes.</summary>
        private struct HighlightMark
        {
            public Item Item;
            public ushort Graphic;
            public int X, Y;
            public float Depth;
            public Color Top;
            public Color? Bottom;
            public float Xray;
            public string Label;
            public ushort LabelHue;
            public Color LabelRule;
            /// <summary>Drawn as a ghost: above the player's ceiling, so not in the world pass.</summary>
            public bool Culled;
        }

        /// <summary>A creature a collector rule names, drawn this frame: its label goes over its head.</summary>
        private struct MobileMark
        {
            public Mobile Mobile;
            /// <summary>Centre of the label and the top of the mobile's overhead stack.</summary>
            public int X, Y;
            public string Label;
            public ushort LabelHue;
            public Color LabelRule;
        }

        private readonly List<HighlightMark> _highlightMarks = new();
        private readonly List<MobileMark> _mobileMarks = new();
        private readonly HashSet<uint> _highlightDrawn = new();
        private readonly List<uint> _culledCandidates = new();
        private uint _culledScanAt;
        private readonly Dictionary<(int x, int y), int> _labelStack = new();
        private readonly Dictionary<(string text, ushort hue), (RenderedText rt, uint used)> _highlightLabels = new();
        private readonly List<(string, ushort)> _labelScratch = new();
        private uint _highlightFrame;

        // Things the depth buffer wrote nearer than an outline: walls, roofs, trees.
        // Greater passes exactly where the outline was hidden, and never writes, so the
        // x-ray ring can't hide anything else.
        private static readonly DepthStencilState XrayDepth = new()
        {
            DepthBufferEnable = true,
            DepthBufferWriteEnable = false,
            DepthBufferFunction = CompareFunction.Greater,
        };

        // How often the world is searched for graded items the render list dropped.
        private const uint CULLED_SCAN_MS = 300;
        // A ghost is a hint, not a sighting.
        private const float CULLED_ALPHA = 0.8f;

        /// <summary>
        /// In Mani Ylem mod: an item drew an outline this frame. Called from ItemView;
        /// records it for the x-ray and label passes and rings the first-sight chime.
        /// </summary>
        internal void RegisterHighlight(Item item, ushort graphic, int x, int y, float depth,
            Color top, Color? bottom, float xray, in HighlightLook look, bool hasLook)
        {
            _highlightDrawn.Add(item.Serial);

            if (xray <= 0f && (!hasLook || look.Label == null))
            {
                if (hasLook) AppraisalHighlights.Announce(_world, item, look);
                return;
            }

            _highlightMarks.Add(new HighlightMark
            {
                Item = item,
                Graphic = graphic,
                X = x,
                Y = y,
                Depth = depth,
                Top = top,
                Bottom = bottom,
                Xray = xray,
                Label = hasLook ? look.Label : null,
                LabelHue = look.LabelHue,
                LabelRule = look.LabelRule,
            });

            if (hasLook) AppraisalHighlights.Announce(_world, item, look);
        }

        /// <summary>
        /// In Mani Ylem mod: a creature a collector rule names drew this frame. Called
        /// from MobileView with the top of its overhead stack; rings the chime.
        /// </summary>
        internal void RegisterMobileHighlight(Mobile mobile, int x, int topY, in HighlightLook look)
        {
            if (look.Label != null)
            {
                _mobileMarks.Add(new MobileMark
                {
                    Mobile = mobile,
                    X = x,
                    Y = topY,
                    Label = look.Label,
                    LabelHue = look.LabelHue,
                    LabelRule = look.LabelRule,
                });
            }
            AppraisalHighlights.Announce(_world, mobile.Serial, look);
        }

        private void BeginHighlightFrame()
        {
            _highlightMarks.Clear();
            _mobileMarks.Clear();
            _highlightDrawn.Clear();
            _highlightFrame++;
        }

        /// <summary>
        /// After the world: each outline again, only where something nearer hid it,
        /// at its x-ray opacity. Just the 1 px ring: the weight copies would land on
        /// the item's own sprite, which also counts as "nearer".
        /// </summary>
        private void DrawHighlightXray(UltimaBatcher2D batcher)
        {
            if (_highlightMarks.Count == 0)
                return;

            batcher.SetStencil(XrayDepth);
            foreach (HighlightMark m in _highlightMarks)
            {
                if (m.Xray <= 0f || m.Item.IsDestroyed)
                    continue;
                if (!GameObject.ResolveItemArt(m.Graphic, m.X, m.Y, out ushort animated, out SpriteInfo art, out Vector2 pos))
                    continue;
                GameObject.DrawArtOutline(batcher, animated, art, pos, m.Xray, m.Top, m.Bottom, 1, m.Depth + RenderDepth.Surface - 0.001f);
            }
            batcher.SetStencil(null);
        }

        /// <summary>
        /// Graded items the render list never drew: upstairs, or under the roof the
        /// player stands beneath. Drawn as a bare ring with no depth test, so a rare
        /// on the floor above shows as a ghost through the ceiling.
        /// </summary>
        private void DrawCulledHighlights(UltimaBatcher2D batcher)
        {
            if (_world.Player == null || !ItemAppraisal.Enabled)
                return;

            int range = AppraisalPalette.XrayCulledRange;
            if (range <= 0)
                return;

            if (Time.Ticks - _culledScanAt >= CULLED_SCAN_MS)
            {
                _culledScanAt = Time.Ticks;
                _culledCandidates.Clear();
                int px = _world.Player.X, py = _world.Player.Y;
                foreach (Item it in _world.Items.Values)
                {
                    // Containers too: TryLook gives them only the loose look, so a chest
                    // someone could carry out of a house upstairs still shows.
                    if (it.IsDestroyed || !it.OnGround || it.IsMulti)
                        continue;
                    if (Math.Max(Math.Abs(it.X - px), Math.Abs(it.Y - py)) > range)
                        continue;
                    if (AppraisalHighlights.TryLook(_world, it, out HighlightLook l) && l.Xray > 0f)
                        _culledCandidates.Add(it.Serial);
                }
            }

            if (_culledCandidates.Count == 0)
                return;

            batcher.SetStencil(DepthStencilState.None);
            foreach (uint serial in _culledCandidates)
            {
                if (_highlightDrawn.Contains(serial) || !_world.Items.TryGetValue(serial, out Item it) || it.IsDestroyed)
                    continue;
                if (!AppraisalHighlights.TryLook(_world, it, out HighlightLook look) || look.Xray <= 0f)
                    continue;

                it.UpdateRealScreenPosition(_offset.X, _offset.Y);
                int x = it.RealScreenPosition.X, y = it.RealScreenPosition.Y;
                if (!GameObject.ResolveItemArt(it.DisplayedGraphic, x, y, out ushort animated, out SpriteInfo art, out Vector2 pos))
                    continue;

                GameObject.DrawArtOutline(batcher, animated, art, pos, look.Xray * CULLED_ALPHA, look.Top, look.Bottom, 1, 0f);
                _highlightMarks.Add(new HighlightMark
                {
                    Item = it,
                    Graphic = it.DisplayedGraphic,
                    X = x,
                    Y = y,
                    Label = look.Label,
                    LabelHue = look.LabelHue,
                    LabelRule = look.LabelRule,
                    Culled = true,
                });
                AppraisalHighlights.Announce(_world, it, look);
            }
            batcher.SetStencil(null);
        }

        /// <summary>
        /// The data a highlight carries, in words over the item: "10/17 Ponytail Palm",
        /// "+4 Int". Overlay pass, under the camera, so it sits over walls and roofs
        /// like the bank grid. A dark plate with a hairline in the grade colour, in the
        /// house panel style; labels sharing a tile stack upward.
        /// </summary>
        private void DrawHighlightLabels(UltimaBatcher2D batcher)
        {
            _labelStack.Clear();

            if (_highlightMarks.Count > 0 || _mobileMarks.Count > 0)
            {
                Texture2D plate = SolidColorTextureCache.GetTexture(new Color(0x0d, 0x10, 0x17));
                var plateHue = new Vector3(0, 0, 0.72f);
                var ruleHue = new Vector3(0, 0, 1f);

                foreach (MobileMark m in _mobileMarks)
                {
                    if (m.Mobile.IsDestroyed)
                        continue;
                    RenderedText rt = HighlightLabel(m.Label, m.LabelHue);
                    int w = rt.Width + 6;
                    int h = rt.Height + 1;
                    int lx = m.X - (w >> 1);
                    int ly = m.Y - h - 2;
                    batcher.Draw(plate, new Rectangle(lx, ly, w, h), plateHue);
                    batcher.Draw(SolidColorTextureCache.GetTexture(m.LabelRule), new Rectangle(lx, ly + h - 1, w, 1), ruleHue);
                    rt.Draw(batcher, lx + 3, ly, 1f);
                }

                foreach (HighlightMark m in _highlightMarks)
                {
                    if (m.Label == null || m.Item.IsDestroyed)
                        continue;
                    if (!GameObject.ResolveItemArt(m.Graphic, m.X, m.Y, out ushort animated, out SpriteInfo art, out Vector2 pos))
                        continue;

                    RenderedText rt = HighlightLabel(m.Label, m.LabelHue);
                    Rectangle bounds = Client.Game.UO.Arts.GetRealArtBounds(animated);
                    int artTop = (int)pos.Y + (bounds.Height > 0 ? bounds.Y : 0);

                    var key = (m.Item.X, m.Item.Y);
                    _labelStack.TryGetValue(key, out int depth);
                    _labelStack[key] = depth + 1;

                    int w = rt.Width + 6;
                    int h = rt.Height + 1;
                    int lx = m.X + 22 - (w >> 1);
                    int ly = artTop - h - 4 - depth * (h + 2);

                    plateHue.Z = m.Culled ? 0.5f : 0.72f;
                    ruleHue.Z = m.Culled ? 0.6f : 1f;
                    batcher.Draw(plate, new Rectangle(lx, ly, w, h), plateHue);
                    batcher.Draw(SolidColorTextureCache.GetTexture(m.LabelRule), new Rectangle(lx, ly + h - 1, w, 1), ruleHue);
                    rt.Draw(batcher, lx + 3, ly, m.Culled ? CULLED_ALPHA : 1f);
                }
            }

            // Drop labels unused for a few seconds.
            if (_highlightFrame % 120 == 0 && _highlightLabels.Count > 0)
            {
                _labelScratch.Clear();
                foreach (var kv in _highlightLabels)
                    if (_highlightFrame - kv.Value.used > 240)
                        _labelScratch.Add(kv.Key);
                foreach (var key in _labelScratch)
                {
                    _highlightLabels[key].rt.Destroy();
                    _highlightLabels.Remove(key);
                }
            }
        }

        private RenderedText HighlightLabel(string text, ushort hue)
        {
            if (!_highlightLabels.TryGetValue((text, hue), out var entry) || entry.rt.IsDestroyed)
                entry.rt = RenderedText.Create(text, hue, 0xFF, true, FontStyle.BlackBorder);
            entry.used = _highlightFrame;
            _highlightLabels[(text, hue)] = entry;
            return entry.rt;
        }
    }
}
