// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.Scenes;
using ClassicUO.Renderer;
using ClassicUO.Utility;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;

namespace ClassicUO.Game.GameObjects
{
    public partial class Mobile
    {
        private const int SIT_OFFSET_Y = 4;
        private EquipConvData? _equipConvData;
        private int _characterFrameStartY;
        private int _startCharacterWaistY;
        private int _startCharacterKneesY;

        internal sealed class StitchinCache
        {
            public uint Generation = uint.MaxValue; // force first compute
            public readonly Dictionary<ushort, ushort> Replacements = new Dictionary<ushort, ushort>();
            public readonly HashSet<ushort> Removals = new HashSet<ushort>();
            public readonly ushort[] LayerEffectiveAnimID = new ushort[30];
            public readonly uint[] LayerCovers = new uint[30];
            public readonly uint[] LayerCoveredBy = new uint[30];
            public readonly uint[] LayerActiveCovers = new uint[30]; // covers from entries with replace/remove
            public uint CoveredLayerMask;
        }

        public override bool Draw(UltimaBatcher2D batcher, int posX, int posY, float depth)
        {
            if (IsDestroyed || !AllowedToDraw || !IsVisible)
            {
                return false;
            }

            // Check if player character should be hidden via Hide HUD system
            if (this is PlayerMobile player && !player.IsVisible)
            {
                return false;
            }

            Profile profile = _profile;
            Managers.AuraManager auraManager = World.AuraManager;
            int clientViewRange = World.ClientViewRange;

            bool charSitting = false;
            ushort overridenHue = 0;

            AnimationsLoader.SittingInfoData seatData = AnimationsLoader.SittingInfoData.Empty;
            _equipConvData = null;
            FrameInfo.X = 0;
            FrameInfo.Y = 0;
            FrameInfo.Width = 0;
            FrameInfo.Height = 0;

            posY -= 3;
            int drawX = posX + (int)Offset.X + 22;
            int drawY = posY + (int)(Offset.Y - Offset.Z) + 22;

            // Cache boolean values
            bool isDead = IsDead;
            bool isHidden = IsHidden;
            bool isPlayer = IsPlayer;
            bool hasShadow = !isDead && !isHidden && profile.ShadowsEnabled;
            bool inParty = InParty;
            bool isHuman = IsHuman;
            bool isGargoyle = IsGargoyle;
            bool isSelected = ReferenceEquals(SelectedObject.Object, this);

            if (auraManager.IsEnabled)
            {
                auraManager.Draw(
                    batcher,
                    drawX,
                    drawY,
                    profile.PartyAura && inParty
                        ? profile.PartyAuraHue
                        : Notoriety.GetHue(NotorietyFlag),
                    depth + 1f
                );
            }

            float finalAlpha = isPlayer && profile.PlayerConstantAlpha != 100 ? profile.PlayerConstantAlpha / 100f : AlphaHue / 255f;
            Vector3 hueVec = ShaderHueTranslator.GetHueVector(0, false, finalAlpha);

            // Simplified hue override logic
            if (isSelected && profile.HighlightGameObjects)
            {
                overridenHue = Constants.HIGHLIGHT_CURRENT_OBJECT_HUE;
                hueVec.Y = 1;
            }
            else if (SelectedObject.HealthbarObject == this)
            {
                overridenHue = Notoriety.GetHue(NotorietyFlag);
            }
            else if (profile.NoColorObjectsOutOfRange && Distance > clientViewRange)
            {
                overridenHue = Constants.OUT_RANGE_COLOR;
                hueVec.Y = 1;
            }
            else if (World.Player.IsDead && profile.EnableBlackWhiteEffect)
            {
                overridenHue = Constants.DEAD_RANGE_COLOR;
                hueVec.Y = 1;
            }
            else if (isHidden)
            {
                overridenHue = profile.HiddenBodyHue;
                hueVec = ShaderHueTranslator.GetHueVector(0, false, (float)profile.HiddenBodyAlpha / 100);
            }
            else
            {
                overridenHue = 0;

                if (isDead && !isHuman)
                {
                    overridenHue = 0x0386;
                }
                else if (!isDead)
                {
                    // Use single if-else chain instead of nested conditions
                    if (profile.HighlightMobilesByInvul && NotorietyFlag != NotorietyFlag.Invulnerable && IsYellowHits)
                    {
                        overridenHue = profile.InvulnerableHue;
                    }
                    else if (profile.HighlightMobilesByParalize && IsParalyzed && NotorietyFlag != NotorietyFlag.Invulnerable)
                    {
                        overridenHue = profile.ParalyzedHue;
                    }
                    else if (profile.HighlightMobilesByPoisoned && IsPoisoned)
                    {
                        overridenHue = profile.PoisonHue;
                    }
                }
            }

            if (!isPlayer)
            {
                Managers.TargetManager targetManager = World.TargetManager;
                if ((targetManager.IsTargeting && isSelected) || (Serial == targetManager.LastAttack && !profile.DisableGrayEnemies))
                    overridenHue = Notoriety.GetHue(NotorietyFlag);
                else if (inParty && profile.OverridePartyAndGuildHue)
                    overridenHue = profile.FriendHue;
            }

            ProcessSteps(out byte dir);
            byte layerDir = dir;

            Client.Game.UO.Animations.GetAnimDirection(ref dir, ref IsFlipped);

            ushort graphic = GetGraphicForAnimation();
            byte animGroup = GetGroupForAnimation(this, graphic, true);
            byte animIndex = AnimIndex;

            Item mount = Mount;
            if (mount != null)
            {
                // Validate mount still exists - don't modify state during rendering
                if (World.Items.Get(mount.Serial) == null)
                {
                    mount = null;
                }
            }

            sbyte mountOffsetY = 0;

            if (isHuman && mount != null && mount.Graphic != 0x3E96)
            {
                ushort mountGraphic = mount.GetGraphicForAnimation();
                byte animGroupMount = 0;

                if (
                    mountGraphic != 0xFFFF
                    && mountGraphic < Client.Game.UO.Animations.MaxAnimationCount
                )
                {
                    bool drawMountAsSingleLayer = false;
                    if (Mounts.TryGet(mount.Graphic, out MountInfo mountInfo))
                    {
                        mountOffsetY = mountInfo.OffsetY;
                        drawMountAsSingleLayer = mountInfo.DrawAsSingleLayer;
                    }

                    // Calculate animation group once
                    animGroupMount = GetGroupForAnimation(this, mountGraphic);

                    if (hasShadow)
                    {
                        DrawInternal(
                            batcher,
                            this,
                            null,
                            drawX,
                            drawY + 10,
                            hueVec,
                            IsFlipped,
                            animIndex,
                            true,
                            graphic,
                            animGroup,
                            dir,
                            isHuman,
                            false,
                            false,
                            false,
                            depth,
                            mountOffsetY,
                            overridenHue,
                            charSitting
                        );

                        DrawInternal(
                            batcher,
                            this,
                            mount,
                            drawX,
                            drawY,
                            hueVec,
                            IsFlipped,
                            animIndex,
                            true,
                            mountGraphic,
                            animGroupMount,
                            dir,
                            isHuman,
                            false,
                            false,
                            false,
                            depth,
                            mountOffsetY,
                            overridenHue,
                            charSitting
                        );
                    }

                    DrawInternal(
                        batcher,
                        this,
                        mount,
                        drawX,
                        drawY,
                        hueVec,
                        IsFlipped,
                        animIndex,
                        false,
                        mountGraphic,
                        animGroupMount,
                        dir,
                        isHuman,
                        false,
                        true,
                        false,
                        depth,
                        mountOffsetY,
                        overridenHue,
                        charSitting,
                        drawAsSingleLayer: drawMountAsSingleLayer
                    );

                    drawY += mountOffsetY;
                }
            }
            else
            {
                if (TryGetSittingInfo(out seatData))
                {
                    animGroup = (byte)PeopleAnimationGroup.Stand;
                    animIndex = 0;

                    ProcessSteps(out dir);

                    Client.Game.UO.FileManager.Animations.FixSittingDirection(
                        ref dir,
                        ref IsFlipped,
                        ref drawX,
                        ref drawY,
                        ref seatData
                    );

                    drawY += SIT_OFFSET_Y;

                    if (dir == 3)
                    {
                        if (isGargoyle)
                        {
                            drawY -= 30 - SIT_OFFSET_Y;
                            animGroup = 42;
                        }
                        else
                        {
                            animGroup = 25;
                        }
                    }
                    else if (isGargoyle)
                    {
                        animGroup = 42;
                    }
                    else
                    {
                        charSitting = true;
                    }
                }
                else if (hasShadow)
                {
                    DrawInternal(
                        batcher,
                        this,
                        null,
                        drawX,
                        drawY,
                        hueVec,
                        IsFlipped,
                        animIndex,
                        true,
                        graphic,
                        animGroup,
                        dir,
                        isHuman,
                        false,
                        false,
                        false,
                        depth,
                        mountOffsetY,
                        overridenHue,
                        charSitting
                    );
                }
            }

            DrawInternal(
                batcher,
                this,
                null,
                drawX,
                drawY,
                hueVec,
                IsFlipped,
                animIndex,
                false,
                graphic,
                animGroup,
                dir,
                isHuman,
                false,
                false,
                isGargoyle,
                depth,
                mountOffsetY,
                overridenHue,
                charSitting,
                outlineColor: OutlineColor
            );

            if (!IsEmpty)
            {
                Stitchin stitchin = Client.Game.UO.FileManager.Stitchin;
                bool useStitchin = isHuman && stitchin != null && stitchin.IsLoaded && Stitchin.Enabled;
                StitchinCache stCache = useStitchin ? EnsureStitchinCache() : null;

                bool swapShirtForTorso = useStitchin && StitchinSwapShirtWithTorso(isHuman);

                for (int i = 0; i < Constants.USED_LAYER_COUNT; i++)
                {
                    Layer layer = LayerOrder.UsedLayers[layerDir, i];

                    if (swapShirtForTorso)
                        layer = layer switch
                        {
                            Layer.Shirt => Layer.Torso,
                            Layer.Torso => Layer.Shirt,
                            _ => layer
                        };

                    Item item = FindItemByLayer(layer);

                    if (item == null)
                    {
                        continue;
                    }

                    if (IsDead && (layer == Layer.Hair || layer == Layer.Beard))
                    {
                        continue;
                    }

                    if (isHuman)
                    {
                        bool hideLayersForSelf = profile.HideLayersForSelf;

                        if (profile.HiddenLayersEnabled && profile.HiddenLayers.Contains((int)layer) && ((hideLayersForSelf && IsPlayer) || !hideLayersForSelf)) continue;

                        ushort effectiveAnimID = item.ItemData.AnimID;
                        bool stitchinReplaced = false;

                        if (stCache != null)
                        {
                            if (stCache.Removals.Contains(effectiveAnimID))
                            {
                                continue;
                            }

                            ushort stEffective = stCache.LayerEffectiveAnimID[(byte)layer];

                            if (stEffective != 0 && stEffective != effectiveAnimID)
                            {
                                effectiveAnimID = stEffective;
                                stitchinReplaced = true;
                            }

                            if (!stitchinReplaced && IsStitchinCovered(stCache, layer))
                            {
                                continue;
                            }
                        }

                        if (IsCovered(this, layer))
                        {
                            continue;
                        }

                        if (effectiveAnimID != 0)
                        {
                            graphic = effectiveAnimID;

                            if (isGargoyle)
                            {
                                FixGargoyleEquipments(ref graphic);
                            }

                            if (
                                Client.Game.UO.FileManager.Animations.EquipConversions.TryGetValue(
                                    Graphic,
                                    out Dictionary<ushort, EquipConvData> map
                                )
                            )
                            {
                                if (map.TryGetValue(effectiveAnimID, out EquipConvData data))
                                {
                                    _equipConvData = data;
                                    graphic = data.Graphic;
                                }
                            }

                            // If stitchin replaced the anim and the replacement has no
                            // frames, fall back to the original item animation.
                            if (stitchinReplaced)
                            {
                                byte checkGroup = isGargoyle && seatData.Graphic == 0
                                    ? GetGroupForAnimation(this, graphic, true)
                                    : animGroup;

                                Span<SpriteInfo> testFrames = Client.Game.UO.Animations.GetAnimationFrames(
                                    graphic, checkGroup, dir, out _, out _, false, false);

                                if (testFrames.Length == 0)
                                {
                                    effectiveAnimID = item.ItemData.AnimID;
                                    graphic = effectiveAnimID;
                                    stitchinReplaced = false;
                                    _equipConvData = null;

                                    if (isGargoyle)
                                    {
                                        FixGargoyleEquipments(ref graphic);
                                    }

                                    if (map != null && map.TryGetValue(effectiveAnimID, out EquipConvData origData))
                                    {
                                        _equipConvData = origData;
                                        graphic = origData.Graphic;
                                    }
                                }
                            }

                            byte group = isGargoyle /*&& item.ItemData.IsWeapon*/
                                        && seatData.Graphic == 0
                                ? GetGroupForAnimation(this, graphic, true)
                                : animGroup;

                            DrawInternal(
                                batcher,
                                this,
                                item,
                                drawX,
                                drawY,
                                hueVec,
                                IsFlipped,
                                animIndex,
                                false,
                                graphic,
                                group,
                                dir,
                                isHuman,
                                !stitchinReplaced,
                                false,
                                isGargoyle,
                                depth,
                                mountOffsetY,
                                overridenHue,
                                charSitting,
                                outlineColor: OutlineColor
                            );

                            if (layer == Layer.Robe && Settings.GlobalSettings.CustomServer == Settings.CustomServers.Eventine)
                            {
                                // Search for item with graphic 0xA413
                                Item aboveRobe = GetItemByGraphic(0xA413);

                                if (aboveRobe != null)
                                {
                                    ushort specialGraphic = aboveRobe.ItemData.AnimID != 0 ? aboveRobe.ItemData.AnimID : aboveRobe.Graphic;
                                    if (isGargoyle)
                                        FixGargoyleEquipments(ref specialGraphic);
                                    byte specialGroup = isGargoyle ? GetGroupForAnimation(this, specialGraphic, true) : animGroup;
                                    DrawInternal(
                                        batcher,
                                        this,
                                        aboveRobe,
                                        drawX,
                                        drawY,
                                        hueVec,
                                        IsFlipped,
                                        animIndex,
                                        false,
                                        specialGraphic,
                                        specialGroup,
                                        dir,
                                        isHuman,
                                        true,
                                        false,
                                        isGargoyle,
                                        depth,
                                        mountOffsetY,
                                        overridenHue,
                                        charSitting,
                                        outlineColor: OutlineColor
                                    );
                                }
                            }
                        }
                        else
                        {
                            if (item.ItemData.IsLight)
                            {
                                GameScene.Instance?.AddLight(this, item, drawX, drawY);
                            }
                        }

                        _equipConvData = null;
                    }
                    else
                    {
                        if (item.ItemData.IsLight)
                        {
                            GameScene.Instance?.AddLight(this, item, drawX, drawY);
                        }
                    }
                }
            }

            FrameInfo.X = Math.Abs(FrameInfo.X);
            FrameInfo.Y = Math.Abs(FrameInfo.Y);
            FrameInfo.Width = FrameInfo.X + FrameInfo.Width;
            FrameInfo.Height = FrameInfo.Y + FrameInfo.Height;
            return true;
        }

        private bool StitchinSwapShirtWithTorso(bool isHuman)
        {
            Stitchin stitchin = Client.Game.UO.FileManager.Stitchin;
            bool useStitchin = isHuman && stitchin != null && stitchin.IsLoaded;
            StitchinCache stCache = useStitchin ? EnsureStitchinCache() : null;

            // If the torso's stitchin entry replaces the shirt AND the torso's
            // coveredBy extends beyond its covers, it's a smaller garment (e.g.
            // female bustier 923) that should be drawn underneath the shirt
            // sleeves.  Full garments (e.g. leather tunic 542) where coveredBy
            // equals covers keep the default order (torso on top).
            bool switchShirtWithTorso = false;
            if (stCache != null)
            {
                Item torsoItem = FindItemByLayer(Layer.Torso);
                Item shirtItem = FindItemByLayer(Layer.Shirt);
                if (torsoItem != null && shirtItem != null)
                {
                    ushort torsoAnim = torsoItem.ItemData.AnimID;
                    ushort shirtAnim = shirtItem.ItemData.AnimID;
                    if (stitchin.TryGetEntry(torsoAnim, out StitchinEntry torsoSt)
                        && torsoSt.Replacements != null
                        && torsoSt.Replacements.ContainsKey(shirtAnim)
                        && (torsoSt.CoveredBy & ~torsoSt.Covers) != 0)
                    {
                        switchShirtWithTorso = true;
                    }
                }
            }

            return switchShirtWithTorso;
        }

        private ushort GetAnimationInfo(Item item, bool isGargoyle, ushort overrideAnimID = 0)
        {
            ushort animID = overrideAnimID != 0 ? overrideAnimID : item.ItemData.AnimID;

            if (animID != 0)
            {
                ushort graphic = animID;

                if (isGargoyle)
                {
                    FixGargoyleEquipments(ref graphic);
                }

                if (
                    Client.Game.UO.FileManager.Animations.EquipConversions.TryGetValue(
                        Graphic,
                        out Dictionary<ushort, EquipConvData> map
                    )
                )
                {
                    if (map.TryGetValue(animID, out EquipConvData data))
                    {
                        _equipConvData = data;
                        graphic = data.Graphic;
                    }
                }

                return graphic;
            }

            return 0xFFFF;
        }

        private static void FixGargoyleEquipments(ref ushort graphic)
        {
            switch (graphic)
            {
                // gargoyle robe
                case 0x01D5:
                    graphic = 0x0156;

                    break;

                // gargoyle dead shroud
                case 0x03CA:
                    graphic = 0x0223;

                    break;

                // gargoyle spellbook
                case 0x03D8:
                    graphic = 329;

                    break;

                // gargoyle necrobook
                case 0x0372:
                    graphic = 330;

                    break;

                // gargoyle chivalry book
                case 0x0374:
                    graphic = 328;

                    break;

                // gargoyle bushido book
                case 0x036F:
                    graphic = 327;

                    break;

                // gargoyle ninjitsu book
                case 0x036E:
                    graphic = 328;

                    break;

                // gargoyle masteries book
                case 0x0426:
                    graphic = 0x042B;

                    break;
                //NOTE: gargoyle mysticism book seems ok. Mha!


                /* into the mobtypes.txt file of 7.0.90+ client version we have:
                 *
                 *   1529 	EQUIPMENT	0		# EQUIP_Shield_Pirate_Male_H
                 *   1530 	EQUIPMENT	0		# EQUIP_Shield_Pirate_Female_H
                 *   1531 	EQUIPMENT	10000	# Equip_Shield_Pirate_Male_G
                 *   1532 	EQUIPMENT	10000	# Equip_Shield_Pirate_Female_G
                 *
                 *   This means that graphic 0xA649 [pirate shield] has 4 tiledata infos.
                 *   Standard client handles it automatically without any issue.
                 *   Maybe it's hardcoded into the client
                 */

                // EQUIP_Shield_Pirate_Male_H
                case 1529:
                    graphic = 1531;

                    break;

                // EQUIP_Shield_Pirate_Female_H
                case 1530:
                    graphic = 1532;

                    break;
            }
        }

        private static bool GetTexture(
            ushort graphic,
            byte animGroup,
            ref byte animIndex,
            byte direction,
            out SpriteInfo spriteInfo,
            out bool isUOP
        )
        {
            spriteInfo = default;

            Span<SpriteInfo> frames = Client.Game.UO.Animations.GetAnimationFrames(
                graphic,
                animGroup,
                direction,
                out _,
                out isUOP
            );

            if (frames.Length == 0)
            {
                return false;
            }

            if (animIndex < 0)
            {
                animIndex = 0;
            }

            animIndex = (byte)(animIndex % frames.Length);

            spriteInfo = frames[animIndex];

            if (spriteInfo.Texture == null)
            {
                return false;
            }

            return true;
        }

        private static void DrawInternal(
            UltimaBatcher2D batcher,
            Mobile owner,
            Item entity,
            int x,
            int y,
            Vector3 hueVec,
            bool mirror,
            byte frameIndex,
            bool hasShadow,
            ushort id,
            byte animGroup,
            byte dir,
            bool isHuman,
            bool isEquip,
            bool isMount,
            bool forceUOP,
            float depth,
            sbyte mountOffset,
            ushort overridedHue,
            bool charIsSitting,
            bool drawAsSingleLayer = false,
            Color? outlineColor = null
        )
        {
            if (id >= Client.Game.UO.Animations.MaxAnimationCount || owner == null)
            {
                return;
            }

            Profiler.EnterContext("Get Anim Frames");
            Span<SpriteInfo> frames = Client.Game.UO.Animations.GetAnimationFrames(
                id,
                animGroup,
                dir,
                out ushort hueFromFile,
                out _,
                isEquip,
                false
            );
            Profiler.ExitContext("Get Anim Frames");

            if (hueFromFile == 0)
            {
                hueFromFile = overridedHue;
            }

            if (frames.Length == 0)
            {
                if (entity != null && entity.ItemData.IsLight) GameScene.Instance.AddLight(owner, owner, x, y);
                return;
            }

            if (frameIndex >= frames.Length)
            {
                frameIndex = (byte)(frames.Length - 1);
            }
            else if (frameIndex < 0)
            {
                frameIndex = 0;
            }

            ref SpriteInfo spriteInfo = ref frames[frameIndex % frames.Length];

            if (spriteInfo.Texture == null)
            {
                // Only continue for sitting characters without entity and no shadow
                if (!(charIsSitting && entity == null && !hasShadow))
                {
                    return;
                }
                // Skip position calculations, go directly to shadow check
            }
            else
            {
                // Position calculations only when texture exists
                if (mirror)
                {
                    x -= (int)((spriteInfo.UV.Width - spriteInfo.Center.X) * owner.Scale);
                }
                else
                {
                    x -= (int)(spriteInfo.Center.X * owner.Scale);
                }

                y -= (int)((spriteInfo.UV.Height + spriteInfo.Center.Y) * owner.Scale);
            }

            if (hasShadow)
            {
                batcher.DrawShadow(
                    spriteInfo.Texture,
                    new Vector2(x, y),
                    spriteInfo.UV,
                    mirror,
                    depth
                );
            }
            else
            {
                ushort hue = overridedHue;
                bool partialHue = false;

                if (hue == 0)
                {
                    hue = entity?.Hue ?? owner.Hue;
                    partialHue = !isMount && entity != null && entity.ItemData.IsPartialHue;

                    if ((hue & 0x8000) != 0)
                    {
                        partialHue = true;
                        hue &= 0x7FFF;
                    }

                    if (hue == 0)
                    {
                        hue = hueFromFile;

                        if (hue == 0 && owner._equipConvData is EquipConvData equipData)
                        {
                            hue = equipData.Color;
                        }

                        partialHue = false;
                    }
                }

                hueVec = ShaderHueTranslator.GetHueVector(hue, partialHue, hueVec.Z);

                if (spriteInfo.Texture != null)
                {
                    var pos = new Vector2(x, y);
                    Rectangle rect = spriteInfo.UV;

                    if (charIsSitting)
                    {
                        Vector3 mod = owner.CalculateSitAnimation(y, entity, isHuman, ref spriteInfo);

                        batcher.DrawCharacterSitted(
                            spriteInfo.Texture,
                            pos,
                            rect,
                            mod,
                            hueVec,
                            mirror,
                            depth + 1f
                        );
                    }
                    else
                    {
                        if (isMount && drawAsSingleLayer)
                        {
                            batcher.Draw(
                                spriteInfo.Texture,
                                pos,
                                rect,
                                hueVec,
                                0f,
                                Vector2.Zero,
                                owner.Scale,
                                mirror ? SpriteEffects.FlipHorizontally : SpriteEffects.None,
                                depth + 1f
                            );
                        }
                        else
                        {
                            int diffY = (spriteInfo.UV.Height + spriteInfo.Center.Y) - mountOffset;

                            int value = Math.Max(1, diffY);
                            int count = Math.Max((spriteInfo.UV.Height / value) + 1, 2);

                            rect.Height = Math.Min(value, rect.Height);
                            int remains = spriteInfo.UV.Height - rect.Height;

                            const int tiles = 2;

                            for (int i = 0; i < count; ++i)
                            {
                                batcher.Draw(
                                    spriteInfo.Texture,
                                    pos,
                                    rect,
                                    hueVec,
                                    0f,
                                    Vector2.Zero,
                                    owner.Scale,
                                    mirror ? SpriteEffects.FlipHorizontally : SpriteEffects.None,
                                    depth + 1f + (i * tiles)
                                );

                                pos.Y += rect.Height * owner.Scale;
                                rect.Y += rect.Height;
                                rect.Height = remains;
                                remains -= rect.Height;
                            }
                        }

                        if (outlineColor.HasValue)
                        {
                            Color oc = outlineColor.Value;
                            var outlineNormal = new Vector3(oc.R / 255f, oc.G / 255f, oc.B / 255f);
                            Vector3 outlineHue = ShaderHueTranslator.GetOutlineHueVector(hueVec.Z);

                            batcher.DrawOutlined(
                                spriteInfo.Texture,
                                new Vector2(x, y),
                                spriteInfo.UV,
                                outlineHue,
                                outlineNormal,
                                0f,
                                Vector2.Zero,
                                owner.Scale,
                                mirror ? SpriteEffects.FlipHorizontally : SpriteEffects.None,
                                depth + 0.999f
                            );
                        }
                    }

                    int xx = (int)(-spriteInfo.Center.X * owner.Scale);
                    int yy = (int)(-(spriteInfo.UV.Height + spriteInfo.Center.Y + 3) * owner.Scale);

                    if (mirror)
                    {
                        xx = (int)(-(spriteInfo.UV.Width - spriteInfo.Center.X) * owner.Scale);
                    }

                    if (xx < owner.FrameInfo.X)
                    {
                        owner.FrameInfo.X = xx;
                    }

                    if (yy < owner.FrameInfo.Y)
                    {
                        owner.FrameInfo.Y = yy;
                    }

                    if (owner.FrameInfo.Width < xx + (int)(spriteInfo.UV.Width * owner.Scale))
                    {
                        owner.FrameInfo.Width = xx + (int)(spriteInfo.UV.Width * owner.Scale);
                    }

                    if (owner.FrameInfo.Height < yy + (int)(spriteInfo.UV.Height * owner.Scale))
                    {
                        owner.FrameInfo.Height = yy + (int)(spriteInfo.UV.Height * owner.Scale);
                    }
                }

                if (entity != null && entity.ItemData.IsLight)
                {
                    GameScene.Instance?.AddLight(owner, entity, mirror ? x + spriteInfo.UV.Width : x, y);
                }
            }
        }

        private Vector3 CalculateSitAnimation(
            int y,
            Item entity,
            bool isHuman,
            ref SpriteInfo spriteInfo
        )
        {
            var mod = new Vector3();

            const float UPPER_BODY_RATIO = 0.35f;
            const float MID_BODY_RATIO = 0.60f;
            const float LOWER_BODY_RATIO = 0.94f;

            if (entity == null && isHuman)
            {
                int frameHeight = spriteInfo.UV.Height;
                if (frameHeight == 0)
                {
                    frameHeight = 61;
                }

                _characterFrameStartY = y - (spriteInfo.Texture != null ? 0 : frameHeight - SIT_OFFSET_Y);
                _startCharacterWaistY = (int)(frameHeight * UPPER_BODY_RATIO) + _characterFrameStartY;
                _startCharacterKneesY = (int)(frameHeight * MID_BODY_RATIO) + _characterFrameStartY;

                if (spriteInfo.Texture == null)
                {
                    return mod;
                }
            }

            mod.X = UPPER_BODY_RATIO;
            mod.Y = MID_BODY_RATIO;
            mod.Z = LOWER_BODY_RATIO;

            if (entity != null)
            {
                float itemsEndY = y + spriteInfo.UV.Height;

                if (y >= _startCharacterWaistY)
                {
                    mod.X = 0;
                }
                else if (itemsEndY <= _startCharacterWaistY)
                {
                    mod.X = 1.0f;
                }
                else
                {
                    float upperBodyDiff = _startCharacterWaistY - y;
                    mod.X = upperBodyDiff / spriteInfo.UV.Height;

                    if (mod.X < 0)
                    {
                        mod.X = 0;
                    }
                }

                if (_startCharacterWaistY >= itemsEndY || y >= _startCharacterKneesY)
                {
                    mod.Y = 0;
                }
                else if (_startCharacterWaistY <= y && itemsEndY <= _startCharacterKneesY)
                {
                    mod.Y = 1.0f;
                }
                else
                {
                    float midBodyDiff;

                    if (y >= _startCharacterWaistY)
                    {
                        midBodyDiff = _startCharacterKneesY - y;
                    }
                    else if (itemsEndY <= _startCharacterKneesY)
                    {
                        midBodyDiff = itemsEndY - _startCharacterWaistY;
                    }
                    else
                    {
                        midBodyDiff = _startCharacterKneesY - _startCharacterWaistY;
                    }

                    mod.Y = mod.X + midBodyDiff / spriteInfo.UV.Height;

                    if (mod.Y < 0)
                    {
                        mod.Y = 0;
                    }
                }

                if (itemsEndY <= _startCharacterKneesY)
                {
                    mod.Z = 0;
                }
                else if (y >= _startCharacterKneesY)
                {
                    mod.Z = 1.0f;
                }
                else
                {
                    float lowerBodyDiff = itemsEndY - _startCharacterKneesY;
                    mod.Z = mod.Y + lowerBodyDiff / spriteInfo.UV.Height;

                    if (mod.Z < 0)
                    {
                        mod.Z = 0;
                    }
                }
            }

            return mod;
        }

        public override bool CheckMouseSelection()
        {
            Point position = RealScreenPosition;
            position.Y -= 3;
            position.X += (int)Offset.X + 22;
            position.Y += (int)(Offset.Y - Offset.Z) + 22;

            Rectangle r = FrameInfo;
            r.X = position.X - r.X;
            r.Y = position.Y - r.Y;

            if (!r.Contains(SelectedObject.TranslatedMousePositionByViewport))
            {
                return false;
            }

            bool isHuman = IsHuman;
            bool isGargoyle =
                Client.Game.UO.Version >= ClientVersion.CV_7000
                && (Graphic == 666 || Graphic == 667 || Graphic == 0x02B7 || Graphic == 0x02B6);

            Renderer.Animations.Animations animations = Client.Game.UO.Animations;

            ProcessSteps(out byte dir);
            byte layerDir = dir;
            bool isFlipped = IsFlipped;
            animations.GetAnimDirection(ref dir, ref isFlipped);

            ushort graphic = GetGraphicForAnimation();
            byte animGroup = GetGroupForAnimation(this, graphic, true);
            byte animIndex = AnimIndex;

            byte animGroupBackup = animGroup;
            byte animIndexBackup = animIndex;

            SpriteInfo spriteInfo;
            bool isUop;

            if (isHuman)
            {
                Item mount = Mount;
                if (mount != null)
                {
                    ushort mountGraphic = mount.GetGraphicForAnimation();

                    if (mountGraphic != 0xFFFF)
                    {
                        byte animGroupMount = GetGroupForAnimation(this, mountGraphic);

                        if (
                            GetTexture(
                                mountGraphic,
                                animGroupMount,
                                ref animIndex,
                                dir,
                                out spriteInfo,
                                out isUop
                            )
                        )
                        {
                            int x =
                                position.X
                                - (int)((
                                    isFlipped
                                        ? spriteInfo.UV.Width - spriteInfo.Center.X
                                        : spriteInfo.Center.X
                                ) * Scale);
                            int y = position.Y - (int)((spriteInfo.UV.Height + spriteInfo.Center.Y) * Scale);

                            if (
                                animations.PixelCheck(
                                    mountGraphic,
                                    animGroupMount,
                                    dir,
                                    isUop,
                                    animIndex,
                                    (int)((isFlipped
                                        ? x
                                            + spriteInfo.UV.Width * Scale
                                            - SelectedObject.TranslatedMousePositionByViewport.X
                                        : SelectedObject.TranslatedMousePositionByViewport.X - x) / Scale),
                                    (int)((SelectedObject.TranslatedMousePositionByViewport.Y - y) / Scale)
                                )
                            )
                            {
                                return true;
                            }

                            if (Mounts.TryGet(mount.Graphic, out MountInfo moutInfo))
                            {
                                position.Y += moutInfo.OffsetY;
                            }
                        }
                    }
                }
            }

            if (GetTexture(graphic, animGroup, ref animIndex, dir, out spriteInfo, out isUop))
            {
                int x =
                    position.X
                    - (int)((isFlipped ? spriteInfo.UV.Width - spriteInfo.Center.X : spriteInfo.Center.X) * Scale);
                int y = position.Y - (int)((spriteInfo.UV.Height + spriteInfo.Center.Y) * Scale);

                if (
                    animations.PixelCheck(
                        graphic,
                        animGroup,
                        dir,
                        isUop,
                        animIndex,
                        (int)((isFlipped
                            ? x
                                + spriteInfo.UV.Width * Scale
                                - SelectedObject.TranslatedMousePositionByViewport.X
                            : SelectedObject.TranslatedMousePositionByViewport.X - x) / Scale),
                        (int)((SelectedObject.TranslatedMousePositionByViewport.Y - y) / Scale)
                    )
                )
                {
                    return true;
                }
            }

            if (!IsEmpty && isHuman)
            {
                Stitchin stitchin = Client.Game.UO.FileManager.Stitchin;
                bool useStitchin = Stitchin.Enabled && stitchin != null && stitchin.IsLoaded;
                StitchinCache stCache = useStitchin ? EnsureStitchinCache() : null;

                for (int i = 0; i < Constants.USED_LAYER_COUNT; i++)
                {
                    Layer layer = LayerOrder.UsedLayers[layerDir, i];
                    Item item = FindItemByLayer(layer);

                    if (
                        item == null
                        || (IsDead && (layer == Layer.Hair || layer == Layer.Beard))
                    )
                    {
                        continue;
                    }

                    ushort effectiveAnimID = item.ItemData.AnimID;
                    bool stitchinReplaced = false;

                    if (stCache != null)
                    {
                        if (stCache.Removals.Contains(effectiveAnimID))
                        {
                            continue;
                        }

                        ushort stEffective = stCache.LayerEffectiveAnimID[(byte)layer];

                        if (stEffective != 0 && stEffective != effectiveAnimID)
                        {
                            effectiveAnimID = stEffective;
                            stitchinReplaced = true;
                        }

                        if (!stitchinReplaced && IsStitchinCovered(stCache, layer))
                        {
                            continue;
                        }
                    }

                    if (IsCovered(this, layer))
                    {
                        continue;
                    }

                    graphic = GetAnimationInfo(item, isGargoyle, useStitchin ? effectiveAnimID : (ushort)0);

                    // If stitchin replaced the anim and no frames exist, fall back to original
                    if (stitchinReplaced && graphic != 0xFFFF)
                    {
                        Span<SpriteInfo> testFrames = Client.Game.UO.Animations.GetAnimationFrames(
                            graphic, animGroupBackup, dir, out _, out _, false, false);

                        if (testFrames.Length == 0)
                        {
                            graphic = GetAnimationInfo(item, isGargoyle);
                        }
                    }

                    if (graphic != 0xFFFF)
                    {
                        animGroup = animGroupBackup;
                        animIndex = animIndexBackup;

                        if (
                            GetTexture(
                                graphic,
                                animGroup,
                                ref animIndex,
                                dir,
                                out spriteInfo,
                                out isUop
                            )
                        )
                        {
                            int x =
                                position.X
                                - (int)((
                                    isFlipped
                                        ? spriteInfo.UV.Width - spriteInfo.Center.X
                                        : spriteInfo.Center.X
                                ) * Scale);
                            int y = position.Y - (int)((spriteInfo.UV.Height + spriteInfo.Center.Y) * Scale);

                            if (
                                animations.PixelCheck(
                                    graphic,
                                    animGroup,
                                    dir,
                                    isUop,
                                    animIndex,
                                    (int)((isFlipped
                                        ? x
                                            + spriteInfo.UV.Width * Scale
                                            - SelectedObject.TranslatedMousePositionByViewport.X
                                        : SelectedObject.TranslatedMousePositionByViewport.X - x) / Scale),
                                    (int)((SelectedObject.TranslatedMousePositionByViewport.Y - y) / Scale)
                                )
                            )
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        internal static bool IsCovered(Mobile mobile, Layer layer)
        {
            if (mobile.IsEmpty)
            {
                return false;
            }

            switch (layer)
            {
                case Layer.Shoes:
                    Item pants = mobile.FindItemByLayer(Layer.Pants);
                    Item robe;

                    //Eventine ignores pants layers
                    if ((Settings.GlobalSettings.CustomServer != Settings.CustomServers.Eventine && mobile.FindItemByLayer(Layer.Legs) != null)
                        || pants != null
                        && (
                            pants.Graphic == 0x1411 /*|| pants.Graphic == 0x141A*/
                        )
                    )
                    {
                        return true;
                    }
                    else
                    {
                        robe = mobile.FindItemByLayer(Layer.Robe);

                        if (
                            pants != null && (pants.Graphic == 0x0513 || pants.Graphic == 0x0514)
                            || robe != null && robe.Graphic == 0x0504
                        )
                        {
                            return true;
                        }
                    }

                    break;

                case Layer.Pants:

                    robe = mobile.FindItemByLayer(Layer.Robe);
                    pants = mobile.FindItemByLayer(Layer.Pants);

                    //Eventine ignores pants layers
                    if ((Settings.GlobalSettings.CustomServer != Settings.CustomServers.Eventine && mobile.FindItemByLayer(Layer.Legs) != null)
                        || robe != null && robe.Graphic == 0x0504
                    )
                    {
                        return true;
                    }

                    if (
                        pants != null
                        && (
                            pants.Graphic == 0x01EB
                            || pants.Graphic == 0x03E5
                            || pants.Graphic == 0x03eB
                        )
                    )
                    {
                        Item skirt = mobile.FindItemByLayer(Layer.Skirt);

                        if (skirt != null && skirt.Graphic != 0x01C7 && skirt.Graphic != 0x01E4)
                        {
                            return true;
                        }

                        if (
                            robe != null
                            && robe.Graphic != 0x0229
                            && (robe.Graphic <= 0x04E7 || robe.Graphic > 0x04EB)
                        )
                        {
                            return true;
                        }
                    }

                    break;

                case Layer.Tunic:
                    robe = mobile.FindItemByLayer(Layer.Robe);
                    Item tunic = mobile.FindItemByLayer(Layer.Tunic);

                    if (tunic != null && tunic.Graphic == 0x0238)
                    {
                        return robe != null
                            && robe.Graphic != 0x9985
                            && robe.Graphic != 0x9986
                            && robe.Graphic != 0xA412
                            && robe.Graphic != 0xA2CB
                            && robe.Graphic != 0xA2CA;
                    }

                    break;

                case Layer.Torso:
                    robe = mobile.FindItemByLayer(Layer.Robe);

                    if (
                        robe != null
                        && robe.Graphic != 0
                        && robe.Graphic != 0x9985
                        && robe.Graphic != 0x9986
                        && robe.Graphic != 0xA412
                        && robe.Graphic != 0xA2CB
                        && robe.Graphic != 0xA2CA
                    )
                    {
                        return true;
                    }
                    else
                    {
                        tunic = mobile.FindItemByLayer(Layer.Tunic);

                        if (tunic != null && tunic.Graphic != 0x1541 && tunic.Graphic != 0x1542)
                        {
                            Item torso = mobile.FindItemByLayer(Layer.Torso);

                            if (
                                torso != null
                                && (torso.Graphic == 0x782A || torso.Graphic == 0x782B)
                            )
                            {
                                return true;
                            }
                        }
                    }

                    break;

                case Layer.Arms:
                    robe = mobile.FindItemByLayer(Layer.Robe);

                    return robe != null
                        && robe.Graphic != 0
                        && robe.Graphic != 0x9985
                        && robe.Graphic != 0x9986
                        && robe.Graphic != 0xA412
                        && robe.Graphic != 0xA2CB
                        && robe.Graphic != 0xA2CA;

                case Layer.Helmet:
                case Layer.Hair:
                    robe = mobile.FindItemByLayer(Layer.Robe);

                    if (robe != null)
                    {
                        if (robe.Graphic > 0x3173)
                        {
                            if (robe.Graphic == 0x4B9D || robe.Graphic == 0x7816)
                            {
                                return true;
                            }
                        }
                        else
                        {
                            if (robe.Graphic <= 0x2687)
                            {
                                if (robe.Graphic < 0x2683)
                                {
                                    return robe.Graphic >= 0x204E && robe.Graphic <= 0x204F;
                                }

                                return true;
                            }

                            if (robe.Graphic == 0x2FB9 || robe.Graphic == 0x3173)
                            {
                                return true;
                            }
                        }
                    }

                    break;
            }

            return false;
        }

        internal StitchinCache EnsureStitchinCache()
        {
            StitchinCache cache = _stitchinCache ??= new StitchinCache();
            if (cache.Generation == _equipmentGeneration)
                return cache;

            ComputeStitchinFull(this, cache);
            cache.Generation = _equipmentGeneration;
            return cache;
        }

        private static void ComputeStitchinFull(Mobile mobile, StitchinCache cache)
        {
            cache.Replacements.Clear();
            cache.Removals.Clear();
            Array.Clear(cache.LayerEffectiveAnimID, 0, cache.LayerEffectiveAnimID.Length);
            Array.Clear(cache.LayerCovers, 0, cache.LayerCovers.Length);
            Array.Clear(cache.LayerCoveredBy, 0, cache.LayerCoveredBy.Length);
            Array.Clear(cache.LayerActiveCovers, 0, cache.LayerActiveCovers.Length);

            Stitchin stitchin = Client.Game.UO.FileManager.Stitchin;

            // First pass: collect replacements and removals from all worn items
            for (Layer lay = Layer.OneHanded; lay < Layer.Mount; lay++)
            {
                Item it = mobile.FindItemByLayer(lay);
                if (it == null || it.ItemData.AnimID == 0)
                {
                    continue;
                }

                if (stitchin.TryGetEntry(it.ItemData.AnimID, out StitchinEntry ent))
                {
                    if (ent.Replacements != null)
                    {
                        foreach (KeyValuePair<ushort, ushort> kvp in ent.Replacements)
                        {
                            cache.Replacements.TryAdd(kvp.Key, kvp.Value);
                        }
                    }

                    if (ent.Removals != null)
                    {
                        foreach (ushort id in ent.Removals)
                        {
                            cache.Removals.Add(id);
                        }
                    }
                }
            }

            // Second pass: resolve effective AnimIDs and look up coverage data
            for (Layer lay = Layer.OneHanded; lay < Layer.Mount; lay++)
            {
                Item it = mobile.FindItemByLayer(lay);
                if (it == null || it.ItemData.AnimID == 0)
                {
                    continue;
                }

                ushort animID = it.ItemData.AnimID;
                ushort effective = cache.Replacements.TryGetValue(animID, out ushort rep) ? rep : animID;

                cache.LayerEffectiveAnimID[(byte)lay] = effective;

                // Coverage data comes from the EFFECTIVE entry (post-replacement).
                // Replaced items have their own entries with updated (typically smaller)
                // coverage — e.g. shirt 538 covers torso+arms, but its replacement
                // 2251 (tunic-only) covers only torso.
                ushort coverageLookupID = effective;
                if (!stitchin.TryGetEntry(coverageLookupID, out StitchinEntry coverageEntry))
                {
                    // Fall back to original entry if the replacement has no entry
                    stitchin.TryGetEntry(animID, out coverageEntry);
                }

                cache.LayerCovers[(byte)lay] = coverageEntry.Covers;
                cache.LayerCoveredBy[(byte)lay] = coverageEntry.CoveredBy;

                    // "Active" entries have replace/remove directives — they
                    // actively manage equipment relationships.  Only coverage
                    // from active entries should hide other items.
                // Use the ORIGINAL entry for this check since that's where
                // the replace/remove directives live.
                if (stitchin.TryGetEntry(animID, out StitchinEntry origEntry))
                {
                    if (origEntry.Replacements != null || origEntry.Removals != null)
                    {
                        cache.LayerActiveCovers[(byte)lay] = coverageEntry.Covers;
                    }
                }
            }

            // Precompute coverage bitmask using only active covers
            uint coveredMask = 0;
            for (byte lay = (byte)Layer.OneHanded; lay < (byte)Layer.Mount; lay++)
            {
                // Weapon layers should never be hidden by body-part coverage
                if (lay == (byte)Layer.OneHanded || lay == (byte)Layer.TwoHanded)
                    continue;

                // Use the item's own covers if available; fall back to coveredBy for
                // items that don't cover any body parts themselves (e.g. hair, beard).
                uint myCovers = cache.LayerCovers[lay];
                uint checkMask = myCovers != 0 ? myCovers : cache.LayerCoveredBy[lay];
                if (checkMask == 0)
                    continue;

                // Check if any single other active item is a STRICT superset of
                // this item's coverage.  Peer items with identical coverage (e.g.
                // shirt and torso both covering TORSO+arms) must not hide each other.
                bool isCovered = false;
                for (byte other = (byte)Layer.OneHanded; other < (byte)Layer.Mount; other++)
                {
                    if (other == (byte)Layer.OneHanded || other == (byte)Layer.TwoHanded)
                        continue;
                    if (other == lay)
                        continue;

                    uint otherCovers = cache.LayerActiveCovers[other];
                    if (otherCovers == 0)
                        continue;

                    if ((checkMask & otherCovers) == checkMask && otherCovers != checkMask)
                    {
                        isCovered = true;
                        break;
                    }
                }

                if (isCovered)
                    coveredMask |= 1u << lay;
            }
            cache.CoveredLayerMask = coveredMask;
        }

        internal static bool IsStitchinCovered(StitchinCache cache, Layer layer) => (cache.CoveredLayerMask & (1u << (byte)layer)) != 0;
    }
}
