// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using ClassicUO.Configuration;
using ClassicUO.IO;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.GameObjects
{
    public enum ObjectHandlesStatus
    {
        NONE,
        OPEN,
        CLOSED,
        DISPLAYING
    }

    public abstract partial class GameObject
    {
        // Constants for tile positioning and depth calculations
        private const int TILE_CENTER_OFFSET = 22;
        private const int TILE_HEIGHT_OFFSET = 44;
        private const int ART_STATIC_OFFSET = 0x4000;
        private const float DEPTH_Z_SCALE = 0.01f;
        private const int DEPTH_Z_OFFSET = 127;
        // Sub-tile depth bands live in RenderDepth. Aliases kept for readability at the call sites below.
        private const float DEPTH_RENDER_OFFSET = RenderDepth.Surface;
        private const float DEPTH_WET_BASE_OFFSET = RenderDepth.SurfaceWetBase;
        private const float DEPTH_SHADOW_OFFSET = RenderDepth.ObjectShadow;

        // Water animation caching for performance
        private static float _cachedWaterSin;
        private static float _cachedWaterCos;
        private static uint _lastWaterAnimTicks;

        public byte AlphaHue;
        public bool AllowedToDraw = true;
        public ObjectHandlesStatus ObjectHandlesStatus;
        public Rectangle FrameInfo;
        public Color? OutlineColor = null;
        // Optional second gradient stop for the outline. When set, the outline blends
        // from OutlineColor at the TOP of the sprite to OutlineColorEnd at the BOTTOM.
        // Used by the container capacity outline to render a fill level.
        public Color? OutlineColorEnd = null;
        protected bool IsFlipped;

        public abstract bool Draw(UltimaBatcher2D batcher, int posX, int posY, float depth);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float CalculateDepthZ()
        {
            int x = X;
            int y = Y;
            int z = PriorityZ;

            // Offsets are in SCREEN coordinates
            // if (Offset.X > 0 && Offset.Y < 0)
            // {
            //     // North
            // }
            // else
            if (Offset.X > 0 && Offset.Y == 0)
            {
                // Northeast
                x++;
            }
            else if (Offset.X > 0 && Offset.Y > 0)
            {
                // East
                z += Math.Max(0, (int)Offset.Z);
                x++;
            }
            else if (Offset.X == 0 && Offset.Y > 0)
            {
                // Southeast
                x++;
                y++;
            }
            else if (Offset.X < 0 && Offset.Y > 0)
            {
                // South
                z += Math.Max(0, (int)Offset.Z);
                y++;
            }
            else if (Offset.X < 0 && Offset.Y == 0)
            {
                // Southwest
                y++;
            }
            // else if (Offset.X < 0 && Offset.Y < 0)
            // {
            //     // West
            // }
            // else if (Offset.X == 0 && Offset.Y < 0)
            // {
            //     // Northwest
            // }

            return (x + y) + (DEPTH_Z_OFFSET + z) * DEPTH_Z_SCALE;
        }

        public Rectangle GetOnScreenRectangle()
        {
            Rectangle prect = Rectangle.Empty;

            prect.X = (int)(RealScreenPosition.X - FrameInfo.X + TILE_CENTER_OFFSET + Offset.X);
            prect.Y = (int)(RealScreenPosition.Y - FrameInfo.Y + TILE_CENTER_OFFSET + (Offset.Y - Offset.Z));
            prect.Width = FrameInfo.Width;
            prect.Height = FrameInfo.Height;

            return prect;
        }

        /// <summary>
        /// Tests if the object is transparent at the given Z coordinate.
        /// Default implementation treats objects as opaque.
        /// </summary>
        /// <param name="z">The Z coordinate to test</param>
        /// <returns>True if transparent at this Z level, false otherwise</returns>
        public virtual bool TransparentTest(int z) => false;

        /// <summary>
        /// Updates cached water animation values if needed and returns the current scale.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector2 GetWaterAnimationScale()
        {
            if (_lastWaterAnimTicks != Time.Ticks)
            {
                _lastWaterAnimTicks = Time.Ticks;
                _cachedWaterSin = (float)Math.Sin(Time.Ticks / 1000f);
                _cachedWaterCos = (float)Math.Cos(Time.Ticks / 1000f);
            }
            return new Vector2(1.1f + _cachedWaterSin * 0.1f, 1.1f + _cachedWaterCos * 0.5f * 0.1f);
        }

        protected static void DrawStatic(
            UltimaBatcher2D batcher,
            ushort graphic,
            int x,
            int y,
            Vector3 hue,
            float depth,
            bool isWet = false,
            Color? outlineColor = null,
            Color? outlineColorEnd = null
        )
        {
            ref readonly SpriteInfo artInfo = ref Client.Game.UO.Arts.GetArt(graphic);

            if (artInfo.Texture != null)
            {
                ref UOFileIndex index = ref Client.Game.UO.FileManager.Arts.File.GetValidRefEntry(graphic + ART_STATIC_OFFSET);
                index.Width = (short)((artInfo.UV.Width >> 1) - TILE_CENTER_OFFSET);
                index.Height = (short)(artInfo.UV.Height - TILE_HEIGHT_OFFSET);

                x -= index.Width;
                y -= index.Height;

                var pos = new Vector2(x, y);
                float renderDepth = depth + DEPTH_RENDER_OFFSET;

                if (isWet)
                {
                    // Draw base layer at slightly lower depth to prevent z-fighting
                    batcher.Draw(
                        artInfo.Texture,
                        pos,
                        artInfo.UV,
                        hue,
                        0f,
                        Vector2.Zero,
                        Vector2.One,
                        SpriteEffects.None,
                        depth + DEPTH_WET_BASE_OFFSET
                    );

                    // Draw animated water layer on top
                    Vector2 scale = GetWaterAnimationScale();
                    batcher.Draw(
                        artInfo.Texture,
                        pos,
                        artInfo.UV,
                        hue,
                        0f,
                        Vector2.Zero,
                        scale,
                        SpriteEffects.None,
                        renderDepth
                    );
                }
                else
                {
                    batcher.Draw(
                        artInfo.Texture,
                        pos,
                        artInfo.UV,
                        hue,
                        0f,
                        Vector2.Zero,
                        Vector2.One,
                        SpriteEffects.None,
                        renderDepth
                    );
                }

                if (outlineColor.HasValue)
                {
                    Color oc = outlineColor.Value;
                    var outlineNormal = new Vector3(oc.R / 255f, oc.G / 255f, oc.B / 255f);
                    // The outline colour's ALPHA channel scales opacity, on top of the
                    // object's own alpha. Callers wanting a flat outline pass an opaque
                    // colour (A=255) and behave exactly as before.
                    Vector3 outlineHue = ShaderHueTranslator.GetOutlineHueVector(hue.Z * (oc.A / 255f));

                    Vector3? endNormal = null;
                    Vector2 outlinePos = pos;
                    Rectangle outlineUV = artInfo.UV;

                    if (outlineColorEnd.HasValue)
                    {
                        Color ec = outlineColorEnd.Value;
                        endNormal = new Vector3(ec.R / 255f, ec.G / 255f, ec.B / 255f);

                        // A gradient MUST be mapped to the sprite's real bounds, not the
                        // art tile. UO tiles carry heavy transparent margin, so quad-mapped
                        // the visible sprite only ever samples the middle of the ramp and
                        // never reaches either endpoint — measured at roughly half the
                        // intended range, with small sprites collapsing to near-flat.
                        Rectangle b = Client.Game.UO.Arts.GetRealArtBounds(graphic);
                        if (b.Width > 0 && b.Height > 0)
                        {
                            outlineUV = new Rectangle(artInfo.UV.X + b.X, artInfo.UV.Y + b.Y, b.Width, b.Height);
                            outlinePos = new Vector2(pos.X + b.X, pos.Y + b.Y);
                        }
                    }

                    batcher.DrawOutlined(
                        artInfo.Texture,
                        outlinePos,
                        outlineUV,
                        outlineHue,
                        outlineNormal,
                        0f,
                        Vector2.Zero,
                        Vector2.One,
                        SpriteEffects.None,
                        renderDepth - 0.001f,
                        endNormal
                    );
                }
            }
        }

        protected static void DrawGump(
            UltimaBatcher2D batcher,
            ushort graphic,
            int x,
            int y,
            Vector3 hue,
            float depth
        )
        {
            ref readonly SpriteInfo gumpInfo = ref Client.Game.UO.Gumps.GetGump(graphic);

            if (gumpInfo.Texture != null)
            {
                float renderDepth = depth + DEPTH_RENDER_OFFSET;
                batcher.Draw(
                    gumpInfo.Texture,
                    new Vector2(x, y),
                    gumpInfo.UV,
                    hue,
                    0f,
                    Vector2.Zero,
                    1f,
                    SpriteEffects.None,
                    renderDepth
                );
            }
        }

        protected static void DrawStaticRotated(
            UltimaBatcher2D batcher,
            ushort graphic,
            int x,
            int y,
            float angle,
            Vector3 hue,
            float depth,
            bool isWet = false
        )
        {
            ref readonly SpriteInfo artInfo = ref Client.Game.UO.Arts.GetArt(graphic);

            if (artInfo.Texture != null)
            {
                ref UOFileIndex index = ref Client.Game.UO.FileManager.Arts.File.GetValidRefEntry(graphic + ART_STATIC_OFFSET);
                index.Width = (short)((artInfo.UV.Width >> 1) - TILE_CENTER_OFFSET);
                index.Height = (short)(artInfo.UV.Height - TILE_HEIGHT_OFFSET);

                float renderDepth = depth + DEPTH_RENDER_OFFSET;

                batcher.Draw(
                    artInfo.Texture,
                    new Rectangle(
                        x - index.Width,
                        y - index.Height,
                        artInfo.UV.Width,
                        artInfo.UV.Height
                    ),
                    artInfo.UV,
                    hue,
                    angle,
                    Vector2.Zero,
                    SpriteEffects.None,
                    renderDepth
                );
            }
        }

        protected static void DrawStaticAnimated(
            UltimaBatcher2D batcher,
            ushort graphic,
            int x,
            int y,
            Vector3 hue,
            bool shadow,
            float depth,
            bool isWet = false,
            Color? outlineColor = null,
            Color? outlineColorEnd = null,
            int outlineWeight = 1
        )
        {
            ref UOFileIndex index = ref Client.Game.UO.FileManager.Arts.File.GetValidRefEntry(graphic + ART_STATIC_OFFSET);

            graphic = (ushort)(graphic + index.AnimOffset);

            ref readonly SpriteInfo artInfo = ref Client.Game.UO.Arts.GetArt(graphic);

            if (artInfo.Texture != null)
            {
                index = ref Client.Game.UO.FileManager.Arts.File.GetValidRefEntry(graphic + ART_STATIC_OFFSET);
                index.Width = (short)((artInfo.UV.Width >> 1) - TILE_CENTER_OFFSET);
                index.Height = (short)(artInfo.UV.Height - TILE_HEIGHT_OFFSET);

                x -= index.Width;
                y -= index.Height;

                var pos = new Vector2(x, y);
                float renderDepth = depth + DEPTH_RENDER_OFFSET;

                if (shadow)
                {
                    batcher.DrawShadow(artInfo.Texture, pos, artInfo.UV, false, depth + DEPTH_SHADOW_OFFSET);
                }

                if (isWet)
                {
                    // Draw base layer at slightly lower depth to prevent z-fighting
                    batcher.Draw(
                        artInfo.Texture,
                        pos,
                        artInfo.UV,
                        hue,
                        0f,
                        Vector2.Zero,
                        Vector2.One,
                        SpriteEffects.None,
                        depth + DEPTH_WET_BASE_OFFSET
                    );

                    // Draw animated water layer on top
                    Vector2 scale = GetWaterAnimationScale();
                    batcher.Draw(
                        artInfo.Texture,
                        pos,
                        artInfo.UV,
                        hue,
                        0f,
                        Vector2.Zero,
                        scale,
                        SpriteEffects.None,
                        renderDepth
                    );
                }
                else
                {
                    batcher.Draw(
                        artInfo.Texture,
                        pos,
                        artInfo.UV,
                        hue,
                        0f,
                        Vector2.Zero,
                        Vector2.One,
                        SpriteEffects.None,
                        renderDepth
                    );
                }

                if (outlineColor.HasValue)
                {
                    DrawArtOutline(batcher, graphic, artInfo, pos, hue.Z, outlineColor.Value, outlineColorEnd, outlineWeight, renderDepth - 0.001f);
                }
            }
        }

        /// <summary>
        /// Where an item's art lands for a draw at (x, y): the animated graphic, its
        /// sprite and the top-left it is drawn from. False when the art isn't loaded.
        /// </summary>
        internal static bool ResolveItemArt(ushort graphic, int x, int y, out ushort animated, out SpriteInfo artInfo, out Vector2 pos)
        {
            ref UOFileIndex index = ref Client.Game.UO.FileManager.Arts.File.GetValidRefEntry(graphic + ART_STATIC_OFFSET);
            animated = (ushort)(graphic + index.AnimOffset);
            artInfo = Client.Game.UO.Arts.GetArt(animated);
            pos = new Vector2(x - ((artInfo.UV.Width >> 1) - TILE_CENTER_OFFSET), y - (artInfo.UV.Height - TILE_HEIGHT_OFFSET));
            return artInfo.Texture != null;
        }

        /// <summary>
        /// The outline ring around a sprite. The shader draws a fixed 1 px ring; a
        /// heavier weight adds copies offset along the four axes, so the ring grows
        /// outward (the copies never cover the sprite, which wrote nearer depth). The
        /// colour's alpha scales opacity on top of <paramref name="alpha"/>; a second
        /// colour runs a gradient top to bottom.
        /// </summary>
        internal static void DrawArtOutline(
            UltimaBatcher2D batcher,
            ushort graphic,
            in SpriteInfo artInfo,
            Vector2 pos,
            float alpha,
            Color top,
            Color? bottom,
            int weight,
            float depth
        )
        {
            var outlineNormal = new Vector3(top.R / 255f, top.G / 255f, top.B / 255f);
            // The outline colour's ALPHA channel scales opacity, on top of the
            // object's own alpha. Callers wanting a flat outline pass an opaque
            // colour (A=255) and behave exactly as before.
            Vector3 outlineHue = ShaderHueTranslator.GetOutlineHueVector(alpha * (top.A / 255f));

            Vector3? endNormal = null;
            Vector2 outlinePos = pos;
            Rectangle outlineUV = artInfo.UV;

            if (bottom.HasValue)
            {
                Color ec = bottom.Value;
                endNormal = new Vector3(ec.R / 255f, ec.G / 255f, ec.B / 255f);

                // A gradient MUST be mapped to the sprite's real bounds, not the
                // art tile. UO tiles carry heavy transparent margin, so quad-mapped
                // the visible sprite only ever samples the middle of the ramp and
                // never reaches either endpoint — measured at roughly half the
                // intended range, with small sprites collapsing to near-flat.
                Rectangle b = Client.Game.UO.Arts.GetRealArtBounds(graphic);
                if (b.Width > 0 && b.Height > 0)
                {
                    // One texel of margin each side, so the ring outside the real
                    // bounds still has quad to land on.
                    int left = Math.Max(0, b.X - 1);
                    int topY = Math.Max(0, b.Y - 1);
                    int right = Math.Min(artInfo.UV.Width, b.X + b.Width + 1);
                    int bot = Math.Min(artInfo.UV.Height, b.Y + b.Height + 1);
                    outlineUV = new Rectangle(artInfo.UV.X + left, artInfo.UV.Y + topY, right - left, bot - topY);
                    outlinePos = new Vector2(pos.X + left, pos.Y + topY);
                }
            }

            for (int i = 0; i < weight * 4 - 3; i++)
            {
                // i = 0 is the ring itself; then +x, -x, +y, -y at 1 px, 2 px ...
                Vector2 offset = Vector2.Zero;
                if (i > 0)
                {
                    int step = (i - 1) / 4 + 1;
                    offset = ((i - 1) % 4) switch
                    {
                        0 => new Vector2(step, 0),
                        1 => new Vector2(-step, 0),
                        2 => new Vector2(0, step),
                        _ => new Vector2(0, -step),
                    };
                }

                batcher.DrawOutlined(
                    artInfo.Texture,
                    outlinePos + offset,
                    outlineUV,
                    outlineHue,
                    outlineNormal,
                    0f,
                    Vector2.Zero,
                    Vector2.One,
                    SpriteEffects.None,
                    depth,
                    endNormal
                );
            }
        }
    }
}
