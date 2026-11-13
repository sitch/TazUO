// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Game.GameObjects;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.Managers
{
    /// <summary>
    /// What a highlighted ground item looks like in the world: outline colours, weight,
    /// how far it shows through walls, its label and its first-sight chime. One place
    /// turns an appraisal into a look, so ItemView, the x-ray pass and the culled pass
    /// draw the same thing.
    /// </summary>
    internal struct HighlightLook
    {
        /// <summary>Top of the outline; A carries the grade's alpha (and pulse).</summary>
        public Color Top;
        /// <summary>Bottom of a gradient, or null for a flat outline.</summary>
        public Color? Bottom;
        public int Weight;
        /// <summary>Opacity where a wall or ceiling hides the outline, 0 for none.</summary>
        public float Xray;
        public HighlightGrade Grade;
        public string Label;
        public ushort LabelHue;
        /// <summary>Accent for the label's underline: the grade colour.</summary>
        public Color LabelRule;
        public ushort Sound;
        /// <summary>A first-sight announcement is due (not house decor).</summary>
        public bool Chime;
        /// <summary>A loose-in-a-house warning: announced in its own words, not as a grade.</summary>
        public bool Warn;
        public string Name;
    }

    /// <summary>Where a loose ground item lies, for the loose highlight.</summary>
    internal enum LoosePlace { None, Town, House, Wild }

    internal static class AppraisalHighlights
    {
        /// <summary>
        /// Ground-loot x-ray for a tier, unidentified or wand outline. A weapon on the
        /// floor of a dungeon room behind a wall is worth seeing, but less than a rare.
        /// </summary>
        private const float LootXray = 0.45f;

        /// <summary>
        /// The look a ground item draws: its appraisal (never for a container, whose
        /// state the chest glow says), else the loose outline when it lies movable
        /// anywhere: a town, a house or the wild. Loose in a house is a warning and wins
        /// over everything, a container's chest glow included: the outline runs from
        /// the warning colour into the item's own, and its label follows the warning's.
        /// </summary>
        public static bool TryLook(World world, Item item, out HighlightLook look)
        {
            look = default;
            if (!ItemAppraisal.Enabled)
                return false;
            LoosePlace place = Loose(world, item);
            if (place != LoosePlace.House)
                _warned.Remove(item.Serial);   // locked again or carried off: warn afresh next time
            AskLandmark(world, item);
            bool appraised = !item.ItemData.IsContainer && TryAppraisalLook(world, item, out look);
            if (place == LoosePlace.House && TryLooseLook(world, item, place, out HighlightLook warn))
            {
                if (appraised)
                {
                    warn.Bottom = new Color(look.Top.R, look.Top.G, look.Top.B, warn.Top.A);
                    warn.Weight = Math.Max(warn.Weight, look.Weight);
                    if (look.Label != null)
                        warn.Label = warn.Label == null ? look.Label : warn.Label + " · " + look.Label;
                }
                look = warn;
                return true;
            }
            return appraised || TryLooseLook(world, item, place, out look);
        }

        /// <summary>
        /// A fixed ground item gets no name until something asks, so a landmark rule
        /// (matched by name) would wait for a hover. Tree art is asked for: the shard's
        /// named trees (the silver tree) stand among a handful of tree items, while
        /// world trees are statics and never come through here. The asks share the
        /// locked-down labels' budget and stop after a few unanswered.
        /// </summary>
        private static void AskLandmark(World world, Item item)
        {
            if (!item.OnGround || item.IsMovable || item.IsMulti || item.IsDestroyed)
                return;
            string art = item.ItemData.Name;
            if (string.IsNullOrEmpty(art) || art.IndexOf("tree", StringComparison.OrdinalIgnoreCase) < 0)
                return;
            if (AppraisalPalette.HasLandmarks)
                LockedDownLabels.Ask(world, item.Serial, recheck: false, movable: false);
        }

        /// <summary>The loose outline alone, for a place.</summary>
        public static bool TryLooseLook(World world, Item item, LoosePlace place, out HighlightLook look)
        {
            look = default;
            if (place == LoosePlace.None)
                return false;
            LooseStyle style = place == LoosePlace.House ? AppraisalPalette.LooseHouse
                : place == LoosePlace.Town ? AppraisalPalette.LooseTown
                : AppraisalPalette.LooseWild;
            if (style.Alpha <= 0f && style.Xray <= 0f)
                return false;
            Color c = style.Color;
            float alpha = style.Alpha * (style.Pulse ? AppraisalPalette.Pulse(Time.Ticks) : 1f);
            look.Top = new Color(c.R, c.G, c.B, (byte)(255 * alpha));
            look.Weight = style.Weight;
            look.Xray = style.Xray;
            look.Label = style.Label;
            look.LabelHue = style.LabelHue;
            look.LabelRule = c;
            if (style.Warns)
            {
                look.Warn = true;
                look.Sound = style.Sound;
                look.Chime = true;
                look.Name = world.OPL.TryGetNameAndData(item.Serial, out string name, out _) && !string.IsNullOrEmpty(name)
                    ? ItemAppraisal.DisplayName(name) : item.ItemData.Name;
            }
            return true;
        }

        /// <summary>
        /// Where the item lies when it sits on the ground, movable (the server's own
        /// flag: what is nailed to a shop table, part of a house or set dressing reads
        /// not-movable) and not labelled locked down: inside a house's footprint, a
        /// guard zone, or else the wild, so a rock or a dropped thing anywhere a hand
        /// can pick it up is outlined. A house wins, since a house in a town is still a
        /// house. Boats are multis too, and skipped. The flag alone lets locked-down
        /// plants and statues through, so the label is asked for, and the item stays
        /// unlit until it answers; a house item is asked again now and then, since a
        /// plant released or locked down keeps its flag. An item the server makes
        /// not-movable (locked down) is forgotten, so the moment it is released and
        /// reads movable again it is asked afresh.
        /// </summary>
        public static LoosePlace Loose(World world, Item item)
        {
            if (!item.OnGround || item.IsMulti || item.IsCorpse || item.IsDestroyed)
                return LoosePlace.None;
            if (!item.IsMovable)
            {
                LockedDownLabels.NotMovable(item.Serial);
                return LoosePlace.None;
            }
            LoosePlace place = InHouse(world, item.X, item.Y) ? LoosePlace.House
                : TownZones.Contains(world.MapIndex, item.X, item.Y) ? LoosePlace.Town
                : LoosePlace.Wild;
            if (LockedDownLabels.Ask(world, item.Serial, recheck: place == LoosePlace.House) != LockedDownLabels.State.Loose)
                return LoosePlace.None;
            return place;
        }

        private static bool InHouse(World world, int x, int y)
        {
            foreach (House house in world.HouseManager.Houses)
            {
                if (house.Components.Count > 0 && house.Components[0].IsMovable)
                    continue;   // a boat
                Item found = world.Items.Get(house.Serial);
                if (found == null || !found.MultiInfo.HasValue)
                    continue;
                Rectangle m = found.MultiInfo.Value;   // offsets: X/Y the min, Width/Height the max
                if (x >= found.X + m.X && x <= found.X + m.Width && y >= found.Y + m.Y && y <= found.Y + m.Height)
                    return true;
            }
            return false;
        }

        // ── Creatures ───────────────────────────────────────────────────────────────

        /// <summary>
        /// A creature a collector rule names (the black sheep): its grade's colour on the
        /// outline, its label over its head, its chime on first sight.
        /// </summary>
        public static bool TryMobileLook(World world, Mobile mobile, out HighlightLook look)
        {
            look = default;
            if (!ItemAppraisal.Enabled || !AppraisalPalette.AnyMobileRules || mobile.IsDestroyed || mobile is PlayerMobile)
                return false;
            if (!AppraisalPalette.MatchMobile(mobile.Name, mobile.Graphic, mobile.Hue, out HighlightGrade grade, out string label))
                return false;

            GradeStyle style = AppraisalPalette.Grade(grade);
            float alpha = style.Alpha * (style.Pulse ? AppraisalPalette.Pulse(Time.Ticks) : 1f);
            Color c = style.Color;
            look.Top = new Color(c.R, c.G, c.B, (byte)(255 * alpha));
            look.Grade = grade;
            look.Weight = style.Weight;
            look.Sound = style.Sound;
            look.LabelRule = style.Color;
            look.LabelHue = style.LabelHue;
            look.Name = string.IsNullOrEmpty(mobile.Name) ? "creature" : mobile.Name;
            if (grade > HighlightGrade.Trace)
            {
                look.Label = label ?? look.Name;
                look.Chime = true;
            }
            return true;
        }

        private static bool TryAppraisalLook(World world, Item item, out HighlightLook look)
        {
            look = default;
            ItemAppraisal.Result r = ItemAppraisal.Appraise(world, item.Serial);

            Color? top = r.Outline ?? r.WandColor;
            if (!top.HasValue && !r.Exceptional)
                return false;

            look.Weight = 1;
            if (r.Rare)
            {
                GradeStyle style = AppraisalPalette.Grade(r.Grade);
                float alpha = style.Alpha * (style.Pulse ? AppraisalPalette.Pulse(Time.Ticks) : 1f);

                look.Grade = r.Grade;
                look.Weight = style.Weight;
                look.Xray = style.Xray;
                look.Sound = style.Sound;
                look.LabelRule = style.Color;
                look.Name = ItemAppraisal.DisplayName(r.Name);

                // Trace is a whisper: no label, no chime.
                if (r.Grade > HighlightGrade.Trace)
                {
                    look.Label = r.Label;
                    look.LabelHue = r.LabelHue;
                    look.Chime = r.Name == null
                                 || r.Name.IndexOf("locked down", StringComparison.OrdinalIgnoreCase) < 0;
                }

                Color c = top ?? style.Color;
                look.Top = new Color(c.R, c.G, c.B, (byte)(255 * alpha));

                // Bonus items run stat colour into grade colour, so a +5 Int sash and a
                // +5 Int wizard's hat of different grades still read apart.
                if (r.BonusKey != null && r.Outline == r.Accent)
                {
                    Color g = style.Color;
                    look.Bottom = new Color(g.R, g.G, g.B, look.Top.A);
                }
            }
            else
            {
                look.Top = top ?? AppraisalPalette.Exceptional;
                look.Xray = top.HasValue ? LootXray : 0f;
                if (r.BorderTier >= 5 && (r.Kind == AppraisalKind.Weapon || r.Kind == AppraisalKind.Armor))
                    look.Weight = 2;
            }

            // Exceptional rides the lower edge of a tier or unidentified outline: an
            // exceptional Ruin weapon is white into copper, a plain Ruin one white.
            if (r.Exceptional && top.HasValue && !look.Bottom.HasValue)
                look.Bottom = AppraisalPalette.Exceptional;

            return true;
        }

        // ── First-sight chime ───────────────────────────────────────────────────────
        private static readonly HashSet<uint> _announced = new();
        private static readonly HashSet<uint> _warned = new();
        private static uint _lastChime;

        /// <summary>
        /// Announce a graded ground item the first time this session it is seen: a
        /// journal line always, the grade's sound at most once per chimeGap, so walking
        /// into a room of rares gives one chime and a list.
        /// </summary>
        public static void Announce(World world, Item item, in HighlightLook look) => Announce(world, item.Serial, look);

        public static void Announce(World world, uint serial, in HighlightLook look)
        {
            if (look.Warn)
            {
                Warn(world, serial, look);
                return;
            }
            if (!look.Chime || look.Grade < HighlightGrade.Prized || !_announced.Add(serial))
                return;

            uint gap = (uint)(AppraisalPalette.ChimeGap * 1000f);
            if (look.Sound != 0 && Time.Ticks - _lastChime >= gap)
            {
                _lastChime = Time.Ticks;
                Client.Game.Audio?.PlaySound(look.Sound);
            }

            string name = look.Name ?? "item";
            string text = "★ " + look.Grade + " · " + name;
            if (look.Label != null && !string.Equals(look.Label, name, StringComparison.OrdinalIgnoreCase))
                text += " · " + look.Label;
            GameActions.Print(world, text, look.LabelHue != 0 ? look.LabelHue : (ushort)946);
        }

        /// <summary>
        /// Loose in a house: a red journal line and the warning sound the first time an
        /// item shows loose, again after it was locked down or left the house and came
        /// back loose. The sound keeps the chime gap, so a room of loose things gives one.
        /// </summary>
        private static void Warn(World world, uint serial, in HighlightLook look)
        {
            if (!_warned.Add(serial))
                return;
            uint gap = (uint)(AppraisalPalette.ChimeGap * 1000f);
            if (look.Sound != 0 && Time.Ticks - _lastChime >= gap)
            {
                _lastChime = Time.Ticks;
                Client.Game.Audio?.PlaySound(look.Sound);
            }
            string what = look.Label ?? "Not locked down";
            int cut = what.IndexOf(" · ", StringComparison.Ordinal);
            if (cut > 0) what = what.Substring(0, cut);
            GameActions.Print(world, "◆ " + what + " · " + (look.Name ?? "item"), look.LabelHue != 0 ? look.LabelHue : (ushort)33);
        }

        public static void ResetSession()
        {
            _announced.Clear();
            _warned.Clear();
        }
    }
}
