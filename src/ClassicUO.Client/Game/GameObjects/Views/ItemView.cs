// SPDX-License-Identifier: BSD-2-Clause


using System;
using System.Collections.Generic;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.IO;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MathHelper = ClassicUO.Utility.MathHelper;
using ClassicUO.Assets;

namespace ClassicUO.Game.GameObjects
{
    public partial class Item
    {
        private static EquipConvData? _equipConvData;
        // Electric teal — outline for lockpickable ground containers that haven't
        // been picked yet (or have content visible — "(1 items, …)").
        private static readonly Color _unpickedChestGlowColor = new Color(0x00, 0xFF, 0xC4);
        // Faded teal — outline for chests we've previously picked and looted (in
        // chestmaster_chests.csv AND tooltip shows "(0 items, 0 stones)"). Darker
        // and slightly desaturated relative to the bright unpicked color, so the
        // family connection is obvious but it doesn't compete visually.
        private static readonly Color _lootedChestGlowColor = new Color(0x2D, 0x75, 0x60);
        // Electric magenta — outline for chests recorded in chestmaster_puzzle_chests.csv,
        // which the script populates when it sees "lock cannot be picked by normal means".
        // Distinct hue so puzzle locks stand out from regular pickable chests at a glance.
        private static readonly Color _puzzleChestGlowColor = new Color(0xFF, 0x00, 0xCC);
        // Per-Item cache of the last *definitive* chest-glow decision and whether we
        // have one yet. Prevents per-frame flicker when the tooltip is transiently
        // incomplete: the shard sends unsolicited MegaCliloc rebroadcasts ~every 1–2 s
        // and single-click bursts rebuild LabelData, both of which can momentarily
        // leave oplText without the "(N items, M stones)" line — which would otherwise
        // re-classify the chest as "count unknown → unpicked → bright teal" for a
        // frame, then back to the resolved color. See project_shard_quirks memory.
        private Color? _chestGlowCache;
        private bool _chestGlowResolved;
        // True while OutlineColor holds a value this Draw() assigned, so we only ever
        // clear our own outline and never one set by another feature.
        private bool _outlineOwned;

        /// <summary>
        /// The universal container outline: every container that can report its contents
        /// gets a faint neutral edge whose OPACITY tracks how full it is. Alpha rather
        /// than hue because the world surface has only one channel — a sprite outline can
        /// express colour and opacity and nothing else, so geometry-based encodings that
        /// work in a grid slot are unavailable here.
        ///
        /// Scaled by sqrt so the low end stays legible: weights observed on this shard run
        /// from 0 to ~16000 stones, and a linear ramp would leave anything under a few
        /// hundred invisible. Capacity is never sent (the shard uses cliloc 1050044, which
        /// carries no maximum), so REF_STONES is a reference point rather than a real cap
        /// and heavier containers simply saturate.
        /// </summary>
        /// <summary>
        /// The universal container outline, encoding BOTH quantities the shard reports.
        ///
        ///   item count  -> the fill LEVEL: how far up the sprite the "filled" colour
        ///                  reaches, rendered as the outline's vertical gradient
        ///   weight      -> the COLOUR of that filled portion, on a LOG scale
        ///
        /// Two channels because the world surface has no others: a sprite outline can
        /// express colour and opacity and nothing else, so pips or bars are unavailable
        /// here. The gradient's two stops are the only way to carry a second value.
        ///
        /// Weight is logarithmic because home containers are effectively unbounded — the
        /// heaviest observed here is ~16000 stones against a 100000 cap — and a linear
        /// ramp would crush everything below a few thousand into one indistinguishable
        /// shade. Log keeps 200 and 20000 clearly apart.
        ///
        /// Returns (top, bottom). Bottom is the filled colour; top blends toward the
        /// empty tint as the level drops, so a nearly-empty container shows colour only
        /// along its base — reading like a level in a vessel.
        /// </summary>
        private static (Color Top, Color Bottom) ContainerCapacityOutline(string oplText)
        {
            const float MAX_STONES = 100000f;   // effective ceiling for home storage
            const int   FULL_ITEMS = 125;       // classic container item limit
            const float EMPTY_A = 0.10f;        // present, but clearly muted

            int count = PickedChestRegistry.TooltipItemCount(oplText) ?? 0;
            int weight = PickedChestRegistry.TooltipWeight(oplText) ?? 0;

            Color empty = AppraisalPalette.ContainerOutline;

            if (count <= 0)
            {
                // Empty: muted rather than merely faint. The neutral tint is darkened
                // toward the background as well as dropped in opacity, so an empty
                // container reads as "nothing here" instead of as a dim version of a
                // full one — opacity alone left it ambiguous against a lightly-loaded
                // container at the bottom of the ramp.
                const float DIM = 0.55f;
                var muted = new Color(
                    (byte)(empty.R * DIM),
                    (byte)(empty.G * DIM),
                    (byte)(empty.B * DIM),
                    (byte)(255 * EMPTY_A));
                return (muted, muted);
            }

            // Weight -> hue along the capacity ramp.
            float w = (float)(Math.Log(1 + Math.Min(weight, MAX_STONES)) / Math.Log(1 + MAX_STONES));
            Color heavy = AppraisalPalette.CapacityRamp(w);

            // Count -> level. Fuller containers also render more opaque, so the two cues
            // reinforce rather than compete.
            float level = Math.Min(1f, count / (float)FULL_ITEMS);
            var alpha = (byte)(255 * (0.32f + 0.42f * level));

            var bottom = new Color(heavy.R, heavy.G, heavy.B, alpha);
            // Top stop interpolates from the empty tint up to the filled colour as the
            // level rises; at level 1 both stops match and the outline is uniform.
            var top = new Color(
                (byte)(empty.R + (heavy.R - empty.R) * level),
                (byte)(empty.G + (heavy.G - empty.G) * level),
                (byte)(empty.B + (heavy.B - empty.B) * level),
                alpha);

            return (top, bottom);
        }

        public override bool Draw(UltimaBatcher2D batcher, int posX, int posY, float depth)
        {
            if (IsDestroyed)
            {
                return false;
            }

            if (
                _isLight
                || DisplayedGraphic >= 0x3E02 && DisplayedGraphic <= 0x3E0B
                || DisplayedGraphic >= 0x3914 && DisplayedGraphic <= 0x3929
            )
            {
                Client.Game.GetScene<GameScene>().AddLight(this, this, posX + 22, posY + 22);
            }

            if (!AllowedToDraw)
            {
                return false;
            }

            //Engine.DebugInfo.ItemsRendered++;
            Vector3 hueVec;

            posX += (int)Offset.X;
            posY += (int)(Offset.Y + Offset.Z);

            float alpha = AlphaHue / 255f;

            if (IsCorpse)
            {
                hueVec = ShaderHueTranslator.GetHueVector(0, false, alpha);
                return DrawCorpse(batcher, posX, posY - 3, hueVec, depth);
            }

            bool isSelected = ReferenceEquals(SelectedObject.Object, this);
            ushort hue = Hue;
            ushort graphic = DisplayedGraphic;
            bool partial = ItemData.IsPartialHue;
            if (ProfileManager.CurrentProfile.AutoAvoidObstacules) {
                if  (StaticFilters.isHumanAndMonster(graphic))
                {
                    if (StaticFilters.IsOutStamina(World))
                    {
                        Client.Game.UO.FileManager.TileData.StaticData[Graphic].SetImpassable(true);
                    }
                    else
                    {
                        Client.Game.UO.FileManager.TileData.StaticData[Graphic].SetImpassable(false);
                    }

                }
            }

            if (OnGround)
            {
                if (ItemData.IsAnimated)
                {
                    if (ProfileManager.CurrentProfile.FieldsType == 2)
                    {
                        if (StaticFilters.IsFireField(Graphic))
                        {
                            graphic = Constants.FIELD_REPLACE_GRAPHIC;
                            hue = 0x0020;
                        }
                        else if (StaticFilters.IsParalyzeField(Graphic))
                        {
                            graphic = Constants.FIELD_REPLACE_GRAPHIC;
                            hue = 0x0058;
                        }
                        else if (StaticFilters.IsEnergyField(Graphic))
                        {
                            graphic = Constants.FIELD_REPLACE_GRAPHIC;
                            hue = 0x0070;
                        }
                        else if (StaticFilters.IsPoisonField(Graphic))
                        {
                            graphic = Constants.FIELD_REPLACE_GRAPHIC;
                            hue = 0x0044;
                        }
                        else if (StaticFilters.IsWallOfStone(Graphic))
                        {
                            graphic = Constants.FIELD_REPLACE_GRAPHIC;
                            hue = 0x038A;
                        }
                    }
                }

                if (ItemData.IsContainer && SelectedObject.SelectedContainer == this)
                {
                    hue = 0x0035;
                    partial = false;
                }
            }

            if (
                ProfileManager.CurrentProfile.HighlightGameObjects
                && isSelected
            )
            {
                hue = Constants.HIGHLIGHT_CURRENT_OBJECT_HUE;
                partial = false;
            }
            else if (
                ProfileManager.CurrentProfile.NoColorObjectsOutOfRange
                && Distance > World.ClientViewRange
            )
            {
                hue = Constants.OUT_RANGE_COLOR;
            }
            else if (World.Player.IsDead && ProfileManager.CurrentProfile.EnableBlackWhiteEffect)
            {
                hue = Constants.DEAD_RANGE_COLOR;
            }
            else
            {
                if (!IsLocked && !IsMulti && isSelected)
                {
                    // TODO: check why i put this.
                    //isPartial = ItemData.Weight == 0xFF;
                    hue = 0x0035;
                }
                else if (IsHidden)
                {
                    hue = 0x038E;
                }
            }

            hueVec = ShaderHueTranslator.GetHueVector(hue, partial, alpha);

            // Glow ground containers that are plausibly lockpickable. Four states:
            //   * Magenta: position is in chestmaster_puzzle_chests.csv — picks won't
            //     work, but it's still a real lockable container worth marking.
            //     Overrides the bright/muted teal so puzzle locks stand out at a glance.
            //   * Bright teal: pickable name, count is null (empty tooltip) or > 0
            //     — unpicked, or currently has items.
            //   * Muted teal: pickable name, count == 0, AND position is in the
            //     picked CSV (chestmaster_chests.csv) — we've looted this one before;
            //     keep it visible but de-emphasized.
            //   * No glow: non-pickable name (ballot box, furniture, books, …),
            //     or count == 0 without a CSV record (bank deco, furniture, …).
            Color? glowColor = null;
            Color? glowColorEnd = null;
            // Any ground container qualifies for the capacity outline — bags, backpacks,
            // pouches and barrels included. The NonChestGraphics blacklist is about
            // LOCKPICKING, not about whether something holds items, so it is applied
            // further down to the teal decision only.
            bool highlightEnabled = ProfileManager.CurrentProfile != null
                && ProfileManager.CurrentProfile.HighlightUnpickedChests
                && OnGround
                && ItemData.IsContainer;

            if (highlightEnabled)
            {
                if (World.OPL.Contains(Serial))
                {
                    World.OPL.TryGetNameAndData(Serial, out string oplName, out string oplData);
                    string oplText = (oplName ?? string.Empty) + "\n" + (oplData ?? string.Empty);

                    if (PickedChestRegistry.IsNeverContainer(oplText))
                    {
                        // Not storage at all (decorative, books, game boards). Pin to
                        // "no glow" — it has no capacity worth reporting.
                        _chestGlowCache = null;
                        _chestGlowResolved = true;
                    }
                    else if (PickedChestRegistry.IsPuzzle(X, Y, World.MapIndex))
                    {
                        // IsPuzzle is coord-based and stable across tooltip flicker.
                        glowColor = _puzzleChestGlowColor;
                        _chestGlowCache = glowColor;
                        _chestGlowResolved = true;
                    }
                    else
                    {
                        int? count = PickedChestRegistry.TooltipItemCount(oplText);
                        bool lockedDown = PickedChestRegistry.IsLockedDown(oplText);

                        // Lockpicking candidacy is narrower than "is a container": a
                        // backpack, pouch or barrel holds things but is never a pick
                        // target. Those still earn the capacity outline below.
                        bool lockCandidate =
                            !PickedChestRegistry.NonChestGraphics.Contains(Graphic)
                            && !PickedChestRegistry.IsNotLockpickable(oplText);

                        // A locked-down container is house furniture and can never be a
                        // lockpicking target. A container that reports its contents at all
                        // is, by definition, one whose lock isn't stopping us — you cannot
                        // see inside a locked chest. So the ONLY state that earns the
                        // pickable-lock teal is "no contents line, and not locked down".
                        //
                        // The old rule glowed on count > 0 ("has items") and on a missing
                        // count at first sighting ("assume unpicked"). Between them every
                        // chest, crate and shelf in the player's own house lit up bright
                        // teal — the exact false positive this replaces.
                        if (lockedDown || count.HasValue)
                        {
                            if (lockCandidate && !lockedDown && count.Value == 0
                                && PickedChestRegistry.IsPicked(X, Y, World.MapIndex))
                            {
                                glowColor = _lootedChestGlowColor;   // we emptied this one
                            }
                            else
                            {
                                (Color t, Color b) = ContainerCapacityOutline(oplText);
                                glowColor = t;
                                glowColorEnd = b;
                            }
                            _chestGlowCache = glowColor;
                            _chestGlowResolved = true;
                        }
                        else if (_chestGlowResolved)
                        {
                            // Tooltip transiently lacks the count line (mid-rebroadcast or
                            // single-click burst). Reuse the last definitive decision
                            // rather than backsliding to "assume unpicked".
                            glowColor = _chestGlowCache;
                        }
                        else
                        {
                            // Genuinely no contents line yet: either still locked, or the
                            // OPL hasn't filled in. Only a lockpick candidate earns teal
                            // here; a closed bag or barrel just gets its capacity outline.
                            // Deliberately not cached, so the next frame with real data wins.
                            if (lockCandidate)
                            {
                                glowColor = _unpickedChestGlowColor;
                            }
                            else
                            {
                                (Color t2, Color b2) = ContainerCapacityOutline(oplText);
                                glowColor = t2;
                                glowColorEnd = b2;
                            }
                        }
                    }
                }
                else if (_chestGlowResolved)
                {
                    // OPL momentarily not Contains(Serial) — reuse the last decision
                    // rather than dropping the outline for a frame.
                    glowColor = _chestGlowCache;
                }
            }

            // Ground loot: appraise dropped weapons/armor/wands so a Vanquishing blade in
            // a pile of junk is visible without hovering every item. Chest glow wins when
            // both apply — a container's pickable state is the more actionable signal, and
            // containers never appraise as weapons anyway.
            if (!glowColor.HasValue && OnGround && !ItemData.IsContainer)
            {
                glowColor = ItemAppraisal.OutlineFor(World, Serial);
            }

            // Clear only what this method set. The old version compared OutlineColor
            // against the three chest colors by value, which silently stopped working
            // the moment a fourth source (appraisal) could set it — a stale outline
            // would then persist forever. Tracking ownership scales to any number of
            // sources and doesn't care what color they chose.
            if (glowColor.HasValue)
            {
                OutlineColor = glowColor.Value;
                OutlineColorEnd = glowColorEnd;
                _outlineOwned = true;
            }
            else if (_outlineOwned)
            {
                OutlineColor = null;
                OutlineColorEnd = null;
                _outlineOwned = false;
            }

            if (!IsMulti && !IsCoin && Amount > 1 && ItemData.IsStackable)
            {
                DrawStaticAnimated(batcher, graphic, posX - 5, posY - 5, hueVec, false, depth, outlineColor: OutlineColor, outlineColorEnd: OutlineColorEnd);
            }

            if (
                !SerialHelper.IsValid(Serial)
                && IsMulti
                && World.TargetManager.TargetingState == CursorTarget.MultiPlacement
            )
            {
                hueVec.Z = 0.5f;
            }

            DrawStaticAnimated(batcher, graphic, posX, posY, hueVec, false, depth, outlineColor: OutlineColor, outlineColorEnd: OutlineColorEnd);

            return true;
        }



        private bool DrawCorpse(
            UltimaBatcher2D batcher,
            int posX,
            int posY,
            Vector3 hueVec,
            float depth
        )
        {
            if (IsDestroyed || World.CorpseManager.Exists(Serial, 0))
            {
                return false;
            }

            posX += 22;
            posY += 22;

            byte direction = (byte)((byte)Layer & 0x7F & 7);
            Client.Game.UO.Animations.GetAnimDirection(ref direction, ref IsFlipped);

            byte animIndex = (byte)AnimIndex;
            ushort graphic = GetGraphicForAnimation();

            Client.Game.UO.Animations.ConvertBodyIfNeeded(ref graphic, isCorpse: IsCorpse);
            AnimationGroupsType animGroup = Client.Game.UO.Animations.GetAnimType(graphic);
            AnimationFlags animFlags = Client.Game.UO.Animations.GetAnimFlags(graphic);
            byte group = Client.Game.UO.FileManager.Animations.GetDeathAction(
                graphic,
                animFlags,
                animGroup,
                UsedLayer
            );

            bool ishuman = IsHumanCorpse;

            DrawLayer(
                batcher,
                posX,
                posY,
                this,
                Layer.Invalid,
                animIndex,
                ishuman,
                Hue,
                IsFlipped,
                hueVec.Z,
                group,
                direction,
                hueVec,
                depth
            );

            for (int i = 0; i < Constants.USED_LAYER_COUNT; i++)
            {
                Layer layer = LayerOrder.UsedLayers[direction, i];

                DrawLayer(
                    batcher,
                    posX,
                    posY,
                    this,
                    layer,
                    animIndex,
                    ishuman,
                    0,
                    IsFlipped,
                    hueVec.Z,
                    group,
                    direction,
                    hueVec,
                    depth
                );
            }

            return true;
        }

        private void DrawLayer(
            UltimaBatcher2D batcher,
            int posX,
            int posY,
            Item owner,
            Layer layer,
            byte animIndex,
            bool ishuman,
            ushort color,
            bool flipped,
            float alpha,
            byte animGroup,
            byte dir,
            Vector3 hueVec,
            float depth
        )
        {
            _equipConvData = null;
            bool ispartialhue = false;

            ushort graphic;

            if (layer == Layer.Invalid)
            {
                graphic = owner.GetGraphicForAnimation();
            }
            else if (ishuman)
            {
                Item itemEquip = owner.FindItemByLayer(layer);

                if (itemEquip == null)
                {
                    return;
                }

                graphic = itemEquip.ItemData.AnimID;
                ispartialhue = itemEquip.ItemData.IsPartialHue;

                if (
                    Client.Game.UO.FileManager.Animations.EquipConversions.TryGetValue(
                        graphic,
                        out Dictionary<ushort, EquipConvData> map
                    )
                )
                {
                    if (map.TryGetValue(graphic, out EquipConvData data))
                    {
                        _equipConvData = data;
                        graphic = data.Graphic;
                    }
                }

                color = itemEquip.Hue;
            }
            else
            {
                return;
            }

            Span<SpriteInfo> frames = Client.Game.UO.Animations.GetAnimationFrames(
                graphic,
                animGroup,
                dir,
                out ushort newHue,
                out _,
                isEquip: layer != Layer.Invalid,
                isCorpse: layer == Layer.Invalid
            );

            if (color == 0)
            {
                color = newHue;
            }

            if (frames.Length == 0)
            {
                return;
            }

            int fc = frames.Length;

            if (fc > 0 && animIndex >= fc)
            {
                animIndex = (byte)(fc - 1);
            }

            if (animIndex < frames.Length)
            {
                ref SpriteInfo spriteInfo = ref frames[animIndex];

                if (spriteInfo.Texture == null)
                {
                    return;
                }

                if (flipped)
                {
                    posX -= spriteInfo.UV.Width - spriteInfo.Center.X;
                }
                else
                {
                    posX -= spriteInfo.Center.X;
                }

                posY -= spriteInfo.UV.Height + spriteInfo.Center.Y;

                if (color == 0)
                {
                    if ((color & 0x8000) != 0)
                    {
                        ispartialhue = true;
                        color &= 0x7FFF;
                    }

                    if (color == 0 && _equipConvData.HasValue)
                    {
                        color = _equipConvData.Value.Color;
                        ispartialhue = false;
                    }
                }

                if (
                    ProfileManager.CurrentProfile.NoColorObjectsOutOfRange
                    && owner.Distance > World.ClientViewRange
                )
                {
                    hueVec = ShaderHueTranslator.GetHueVector(
                        Constants.OUT_RANGE_COLOR + 1,
                        false,
                        1
                    );
                }
                else if (
                    World.Player.IsDead && ProfileManager.CurrentProfile.EnableBlackWhiteEffect
                )
                {
                    hueVec = ShaderHueTranslator.GetHueVector(
                        Constants.DEAD_RANGE_COLOR + 1,
                        false,
                        1
                    );
                }
                else
                {
                    if ((ProfileManager.CurrentProfile.GridLootType > 0 || ProfileManager.CurrentProfile.UseGridLayoutContainerGumps) && SelectedObject.CorpseObject == owner)
                    {
                        color = 0x0034;
                    }
                    else if (
                        ProfileManager.CurrentProfile.HighlightGameObjects
                        && ReferenceEquals(SelectedObject.Object, owner)
                    )
                    {
                        color = Constants.HIGHLIGHT_CURRENT_OBJECT_HUE;
                    }

                    hueVec = ShaderHueTranslator.GetHueVector(color, ispartialhue, alpha);
                }

                var pos = new Vector2(posX, posY);
                Rectangle rect = spriteInfo.UV;

                int diffY = (spriteInfo.UV.Height + spriteInfo.Center.Y);
                int value = /*!isMounted && diffX <= 44 ? spriteInfo.UV.Height * 2 :*/
                Math.Max(1, diffY);
                int count = Math.Max((spriteInfo.UV.Height / value) + 1, 2);

                rect.Height = Math.Min(value, rect.Height);
                int remains = spriteInfo.UV.Height - rect.Height;

                int tiles = (byte)owner.Direction % 2 == 0 ? 2 : 2;

                for (int i = 0; i < count; ++i)
                {
                    //hueVec.Y = 1;
                    //hueVec.X = 0x44 + (i * 20);

                    batcher.Draw(
                        spriteInfo.Texture,
                        pos,
                        rect,
                        hueVec,
                        0f,
                        Vector2.Zero,
                        1f,
                        flipped ? SpriteEffects.FlipHorizontally : SpriteEffects.None,
                        depth + 1f + (i * tiles)
                    //depth + (i * tiles) + (owner.PriorityZ * 0.001f)
                    );

                    pos.Y += rect.Height;
                    rect.Y += rect.Height;
                    rect.Height = remains; // Math.Min(value, remains);
                    remains -= rect.Height;
                }
            }
        }

        public override bool CheckMouseSelection()
        {
            if (!IsCorpse)
            {
                if (
                    ReferenceEquals(SelectedObject.Object, this)
                    || World.TargetManager.TargetingState == CursorTarget.MultiPlacement
                )
                {
                    return false;
                }

                ushort graphic = DisplayedGraphic;

                if (OnGround && ItemData.IsAnimated)
                {
                    if (
                        ProfileManager.CurrentProfile.FieldsType == 2
                        && (
                            StaticFilters.IsFireField(Graphic)
                            || StaticFilters.IsParalyzeField(Graphic)
                            || StaticFilters.IsEnergyField(Graphic)
                            || StaticFilters.IsPoisonField(Graphic)
                            || StaticFilters.IsWallOfStone(Graphic)
                        )
                    )
                    {
                        graphic = Constants.FIELD_REPLACE_GRAPHIC;
                    }
                    else
                    {
                        ref UOFileIndex index = ref Client.Game.UO.FileManager.Arts.File.GetValidRefEntry(
                            graphic + 0x4000
                        );

                        graphic += (ushort)index.AnimOffset;
                    }
                }

                if (Client.Game.UO.Arts.GetArt(graphic).Texture != null)
                {
                    ref UOFileIndex index = ref Client.Game.UO.FileManager.Arts.File.GetValidRefEntry(graphic + 0x4000);

                    Point position = RealScreenPosition;
                    position.X += (int)Offset.X;
                    position.Y += (int)(Offset.Y + Offset.Z);
                    position.X -= index.Width;
                    position.Y -= index.Height;

                    if (
                        Client.Game.UO.Arts.PixelCheck(
                            graphic,
                            SelectedObject.TranslatedMousePositionByViewport.X - position.X,
                            SelectedObject.TranslatedMousePositionByViewport.Y - position.Y
                        )
                    )
                    {
                        return true;
                    }
                    else if (!IsMulti && !IsCoin && Amount > 1 && ItemData.IsStackable)
                    {
                        if (
                            Client.Game.UO.Arts.PixelCheck(
                                graphic,
                                SelectedObject.TranslatedMousePositionByViewport.X - position.X + 5,
                                SelectedObject.TranslatedMousePositionByViewport.Y - position.Y + 5
                            )
                        )
                        {
                            return true;
                        }
                    }
                }
            }
            else
            {
                if (!SerialHelper.IsValid(Serial))
                {
                    return false;
                }

                if (ReferenceEquals(SelectedObject.Object, this))
                {
                    return true;
                }

                Renderer.Animations.Animations animations = Client.Game.UO.Animations;

                Point position = RealScreenPosition;
                position.X += 22;
                position.Y += 22;

                byte direction = (byte)((byte)Layer & 0x7F & 7);
                animations.GetAnimDirection(ref direction, ref IsFlipped);
                byte animIndex = AnimIndex;
                bool ishuman =
                    MathHelper.InRange(Amount, 0x0190, 0x0193)
                    || MathHelper.InRange(Amount, 0x00B7, 0x00BA)
                    || MathHelper.InRange(Amount, 0x025D, 0x0260)
                    || MathHelper.InRange(Amount, 0x029A, 0x029B)
                    || MathHelper.InRange(Amount, 0x02B6, 0x02B7)
                    || Amount == 0x03DB
                    || Amount == 0x03DF
                    || Amount == 0x03E2
                    || Amount == 0x02E8
                    || Amount == 0x02E9;

                for (int i = -1; i < Constants.USED_LAYER_COUNT; i++)
                {
                    // yes im lazy
                    Layer layer = i == -1 ? Layer.Invalid : LayerOrder.UsedLayers[direction, i];

                    ushort graphic;

                    if (layer == Layer.Invalid)
                    {
                        graphic = GetGraphicForAnimation();
                    }
                    else if (ishuman)
                    {
                        Item itemEquip = FindItemByLayer(layer);

                        if (itemEquip == null)
                        {
                            continue;
                        }

                        graphic = itemEquip.ItemData.AnimID;

                        if (
                            Client.Game.UO.FileManager.Animations.EquipConversions.TryGetValue(
                                graphic,
                                out Dictionary<ushort, EquipConvData> map
                            )
                        )
                        {
                            if (map.TryGetValue(graphic, out EquipConvData data))
                            {
                                _equipConvData = data;
                                graphic = data.Graphic;
                            }
                        }
                    }
                    else
                    {
                        continue;
                    }

                    animations.ConvertBodyIfNeeded(ref graphic, isCorpse: IsCorpse);
                    AnimationGroupsType animGroup = animations.GetAnimType(graphic);
                    AnimationFlags animFlags = animations.GetAnimFlags(graphic);
                    byte group = Client.Game.UO.FileManager.Animations.GetDeathAction(
                        graphic,
                        animFlags,
                        animGroup,
                        UsedLayer
                    );
                    Span<SpriteInfo> frames = animations.GetAnimationFrames(
                        graphic,
                        group,
                        direction,
                        out _,
                        out bool isUOP,
                        false,
                        IsCorpse
                    );

                    //IsEmpty should already check length == 0, however we were somehow getting zero length frames still, adding a .Length == 0 fixed it.
                    if (frames.IsEmpty || frames.Length == 0)
                    {
                        continue;
                    }

                    if (animIndex < 0)
                    {
                        animIndex = 0;
                    }

                    if (animIndex >= 0)
                    {
                        animIndex = (byte)(animIndex % frames.Length);
                    }

                    ref SpriteInfo spriteInfo = ref frames[animIndex];

                    if (spriteInfo.Texture != null)
                    {
                        int x =
                            position.X
                            - (
                                IsFlipped
                                    ? spriteInfo.UV.Width - spriteInfo.Center.X
                                    : spriteInfo.Center.X
                            );
                        int y = position.Y - (spriteInfo.UV.Height + spriteInfo.Center.Y);

                        if (
                            animations.PixelCheck(
                                graphic,
                                group,
                                direction,
                                isUOP,
                                animIndex,
                                IsFlipped
                                    ? x
                                        + spriteInfo.UV.Width
                                        - SelectedObject.TranslatedMousePositionByViewport.X
                                    : SelectedObject.TranslatedMousePositionByViewport.X - x,
                                SelectedObject.TranslatedMousePositionByViewport.Y - y
                            )
                        )
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
    }
}
