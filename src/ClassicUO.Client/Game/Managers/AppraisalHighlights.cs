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
        /// <summary>A loose-in-a-house or stealable warning: announced in its own words, not as a grade.</summary>
        public bool Warn;
        public string Name;
    }

    /// <summary>
    /// Where a loose ground item lies, for the loose highlight; or Stealable, a fixed
    /// item a thief can take.
    /// </summary>
    internal enum LoosePlace { None, Town, House, Wild, Stealable }

    internal static class AppraisalHighlights
    {
        /// <summary>
        /// Ground-loot x-ray for a tier, unidentified or wand outline. A weapon on the
        /// floor of a dungeon room behind a wall is worth seeing, but less than a rare.
        /// </summary>
        private const float LootXray = 0.45f;

        /// <summary>World map markers for landmarks (the silver tree), through <see cref="MapMarks"/>.</summary>
        public const string LANDMARKS_FILE = "in_mani_ylem_landmarks.csv";
        /// <summary>Silver, for the silver tree and its kind.</summary>
        private const string LANDMARK_COLOR = "#D8DCE8";

        /// <summary>
        /// The look a ground item draws: its appraisal (never for a container, whose
        /// state the chest glow says), else the loose outline when it lies movable
        /// anywhere: a town, a house or the wild. Loose in a house, and stealable, are
        /// warnings and win over everything, a container's chest glow included: the
        /// outline runs from the warning colour into the item's own, and its label
        /// follows the warning's.
        /// </summary>
        public static bool TryLook(World world, Item item, out HighlightLook look)
        {
            look = default;
            if (!ItemAppraisal.Enabled)
                return false;
            LoosePlace place = Loose(world, item);
            if (!Wins(place))
                _warned.Remove(item.Serial);   // locked again or carried off: warn afresh next time
            AskLandmark(world, item);
            bool appraised = !item.ItemData.IsContainer && TryAppraisalLook(world, item, out look);
            if (Wins(place) && TryLooseLook(world, item, place, out HighlightLook warn))
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

        /// <summary>A place whose outline wins over a chest glow and an appraisal.</summary>
        public static bool Wins(LoosePlace place) => place == LoosePlace.House || place == LoosePlace.Stealable;

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
                : place == LoosePlace.Stealable ? AppraisalPalette.LooseStealable
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
        /// plants and statues through, so the label is asked for; a house item stays
        /// unlit until it answers and is asked again now and then, since a plant
        /// released or locked down keeps its flag. Nothing outside a house can be
        /// locked down, so a town's or the wild's item is outlined at once and only an
        /// answer saying "locked down" (a house's edge the footprint missed) puts it
        /// out. An item the server makes not-movable (locked down) is forgotten, so the
        /// moment it is released and reads movable again it is asked afresh. The shard
        /// says "locked down" only to someone standing in the house, so a house item is
        /// asked, and lights, only while the player is inside that house; from the
        /// street it keeps what it showed last (dark until first seen from inside).
        ///
        /// A not-movable item on one of the stealable spots is Stealable. A house sign
        /// (<see cref="HouseSigns"/>) is never anything: it reads movable and is never
        /// locked down, so sign art waits for its answer wherever it hangs.
        /// </summary>
        public static LoosePlace Loose(World world, Item item)
        {
            if (!item.OnGround || item.IsMulti || item.IsCorpse || item.IsDestroyed)
                return LoosePlace.None;
            if (HouseSigns.IsKnownSign(item.Serial) || HouseSigns.Check(world, item))
                return LoosePlace.None;   // a house sign reads movable; it goes on the map instead
            if (!item.IsMovable)
            {
                LockedDownLabels.NotMovable(item.Serial);
                return IsStealableSpot(world.MapIndex, item.X, item.Y) ? LoosePlace.Stealable : LoosePlace.None;
            }
            uint house = HouseAt(world, item.X, item.Y, 0);
            LoosePlace place = house != 0 ? LoosePlace.House
                : TownZones.Contains(world.MapIndex, item.X, item.Y) ? LoosePlace.Town
                : LoosePlace.Wild;
            // The shard says "locked down" only to someone inside that house, so a
            // house item's bare answer counts only from in there.
            bool trusted = house == 0 || PlayerHouse(world) == house;
            LockedDownLabels.State state = LockedDownLabels.Ask(world, item.Serial, recheck: place == LoosePlace.House, trusted: trusted);
            if (state == LockedDownLabels.State.LockedDown
                || (state == LockedDownLabels.State.Unknown && (place == LoosePlace.House || HouseSigns.IsSignArt(item))))
                return LoosePlace.None;
            return HouseSigns.Check(world, item) ? LoosePlace.None : place;
        }

        // ── Stealable ───────────────────────────────────────────────────────────────

        /// <summary>Malas, the only map RunUO's stealable artifacts spawn on.</summary>
        private const int StealableMap = 3;

        /// <summary>
        /// Where RunUO's StealableArtifactsSpawner sets its artifacts (runuo/runuo
        /// 71b2794, Scripts/Items/Decoration Artifacts): the Doom gauntlet (x 250-500)
        /// and the Tokuno-styled rooms (x 0-190), all on Malas. Each is made
        /// not-movable and moved to exactly this tile, respawning 72 min to 154 days
        /// after it is stolen. The client sees no "stealable" flag on any item, and in
        /// stock RunUO nothing else on the ground can be stolen (Stealing refuses a
        /// not-movable item unless the spawner owns it; a faction sigil aside), so the
        /// spot is the whole test. Two artifacts share 423,28.
        /// </summary>
        private static readonly HashSet<(int X, int Y)> StealableSpots = new()
        {
            (317, 56), // rock
            (360, 31), // skull candle
            (369, 372), // bottle
            (378, 372), // damaged books
            (432, 16), // stretched hide
            (489, 9), // brazier
            (471, 96), // lamp post
            (421, 198), // books north
            (431, 189), // books west
            (435, 196), // books face down
            (447, 9), // studded leggings
            (423, 28), // egg case
            (347, 44), // skinned goat
            (497, 57), // gruesome standard
            (381, 375), // bloody water
            (489, 369), // tarot cards
            (497, 369), // backpack
            (475, 23), // studded tunic
            (423, 28), // cocoon
            (354, 36), // skinned deer
            (433, 11), // saddle
            (403, 31), // leather tunic
            (257, 70), // zyronic claw
            (354, 176), // titans hammer
            (369, 389), // blade of the righteous
            (467, 92), // inquisitors resolution
            (487, 364), // ruined painting
            (18, 110), // basket 1
            (66, 114), // basket 2
            (63, 12), // basket 4
            (5, 29), // basket 5 north
            (30, 81), // basket 5 west
            (115, 7), // urn 1
            (85, 13), // urn 2
            (110, 53), // sculpture 1
            (108, 37), // sculpture 2
            (121, 14), // teapot north
            (121, 115), // teapot west
            (84, 40), // tower lantern
            (94, 7), // man statuette south
            (113, 640), // basket 3 north
            (102, 355), // basket 3 west
            (99, 370), // basket 6
            (100, 357), // zen rock 1
            (73, 473), // fan north
            (99, 372), // fan west
            (92, 326), // bowls vertical
            (97, 470), // zen rock 2
            (103, 691), // zen rock 3
            (103, 336), // painting 1 north
            (59, 381), // painting 1 west
            (84, 401), // painting 2 north
            (59, 392), // painting 2 west
            (107, 483), // triple fan north
            (50, 475), // triple fan west
            (107, 460), // bowl
            (90, 502), // cups
            (107, 688), // bowls horizontal
            (112, 676), // sake
            (135, 614), // sword display 1 north
            (50, 482), // sword display 1 west
            (119, 672), // painting 3
            (90, 326), // painting 4 north
            (99, 354), // painting 4 west
            (179, 652), // sword display 2 north
            (118, 627), // sword display 2 west
            (90, 483), // flowers
            (71, 562), // dolphin left
            (102, 677), // dolphin right
            (61, 499), // sword display 3 south
            (182, 669), // sword display 3 east
            (162, 647), // sword display 4 west
            (124, 624), // painting 5 north
            (146, 649), // painting 5 west
            (100, 488), // sword display 4 north
            (175, 606), // sword display 5 north
            (157, 608), // sword display 5 west
            (187, 643), // painting 6 north
            (146, 623), // painting 6 west
            (178, 629), // man statuette east
        };

        public static bool IsStealableSpot(int map, int x, int y) =>
            map == StealableMap && StealableSpots.Contains((x, y));

        /// <summary>
        /// The house whose footprint holds x,y, 0 for none. <paramref name="inset"/>
        /// tiles of the edge don't count: RunUO's house region (where the shard says
        /// "locked down") leaves out the front steps the footprint includes.
        /// </summary>
        private static uint HouseAt(World world, int x, int y, int inset)
        {
            foreach (House house in world.HouseManager.Houses)
            {
                if (house.Components.Count > 0 && house.Components[0].IsMovable)
                    continue;   // a boat
                Item found = world.Items.Get(house.Serial);
                if (found == null || !found.MultiInfo.HasValue)
                    continue;
                Rectangle m = found.MultiInfo.Value;   // offsets: X/Y the min, Width/Height the max
                if (x >= found.X + m.X + inset && x <= found.X + m.Width - inset
                    && y >= found.Y + m.Y + inset && y <= found.Y + m.Height - inset)
                    return house.Serial;
            }
            return 0;
        }

        private static uint _playerHouse;
        private static uint _playerHouseTick;
        private static (int X, int Y) _playerHouseAt = (-1, -1);

        /// <summary>The house the player stands well inside, 0 for none; worked out once a frame.</summary>
        private static uint PlayerHouse(World world)
        {
            PlayerMobile p = world.Player;
            if (p == null)
                return 0;
            if (_playerHouseTick != Time.Ticks || _playerHouseAt != (p.X, p.Y))
            {
                _playerHouseTick = Time.Ticks;
                _playerHouseAt = (p.X, p.Y);
                _playerHouse = HouseAt(world, p.X, p.Y, 1);
            }
            return _playerHouse;
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

                // A fixed thing with a grade is a landmark (the silver tree): it goes on
                // the world map where it stands.
                if (r.Fixed && item.OnGround && r.Grade > HighlightGrade.None && !string.IsNullOrEmpty(look.Name))
                    MapMarks.Note(LANDMARKS_FILE, item.X, item.Y, world.MapIndex, look.Name, LANDMARK_COLOR, out _);

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
