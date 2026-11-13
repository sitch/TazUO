using ClassicUO.Game.Managers;
using Xunit;

namespace ClassicUO.UnitTests.Game.Managers
{
    public class AppraisalRulesTest
    {
        private static ItemAppraisal.Result Graded(string text, ushort graphic, ushort hue = 0, bool onGround = true,
            bool isFixed = false)
        {
            var r = ItemAppraisal.Parse(text);
            r.Fixed = isFixed;
            string[] lines = text.Split('\n');
            r.Name = lines.Length > 1 ? lines[0] + "\n" + lines[1] : lines[0];
            r.Graphic = graphic;
            r.Hue = hue;
            r.OnGround = onGround;
            return ItemAppraisal.Recolor(r);
        }

        // ── Spell scrolls ───────────────────────────────────────────────────────────

        [Theory]
        [InlineData("Magic Arrow", 0x1F32)]
        [InlineData("Magic Trap", 0x1F39)]
        [InlineData("Magic Untrap", 0x1F3A)]
        [InlineData("Magic Lock", 0x1F43)]
        [InlineData("Magic Unlock", 0x1F4E)]
        [InlineData("Magic Reflection", 0x1F50)]
        public void Magic_spell_scrolls_are_not_magic_clothing(string name, int graphic)
        {
            var r = Graded(name, (ushort)graphic);

            Assert.Equal(HighlightGrade.None, r.Grade);
            Assert.False(r.Rare);
        }

        [Fact]
        public void Magic_clothing_is_still_prized()
        {
            Assert.Equal(HighlightGrade.Prized, Graded("magic surcoat", 0x1FFD).Grade);
        }

        // ── Landmarks ───────────────────────────────────────────────────────────────

        [Fact]
        public void Silver_tree_is_called_out_though_nobody_can_move_it()
        {
            var r = Graded("a silver tree", 0x0CD6, isFixed: true);

            Assert.Equal(HighlightGrade.Callout, r.Grade);
            Assert.Equal("\u2605 Silver Tree", r.Label);
        }

        [Fact]
        public void A_fixed_collectible_is_still_set_dressing()
        {
            Assert.Equal(HighlightGrade.None, Graded("", 0x0F81, isFixed: true).Grade);
            Assert.Equal(HighlightGrade.None, Graded("magic surcoat", 0x1FFD, isFixed: true).Grade);
        }

        [Fact]
        public void A_fixed_ordinary_tree_is_nothing()
        {
            Assert.Equal(HighlightGrade.None, Graded("a cedar tree", 0x0CD6, isFixed: true).Grade);
        }

        // ── Fertile dirt and black wool ─────────────────────────────────────────────

        [Fact]
        public void Fertile_dirt_is_rare_before_its_tooltip_arrives()
        {
            Assert.Equal(HighlightGrade.Rare, Graded("", 0x0F81).Grade);
            Assert.Equal(HighlightGrade.Rare, Graded("fertile dirt : 5", 0x0F81, onGround: false).Grade);
        }

        [Fact]
        public void Hued_wool_is_the_black_sheeps_and_plain_wool_is_nothing()
        {
            var black = Graded("wool : 2", 0x0DF8, hue: 0x0455);

            Assert.Equal(HighlightGrade.Prized, black.Grade);
            Assert.Equal("Black Wool", black.Label);
            Assert.Equal(HighlightGrade.None, Graded("wool : 2", 0x0DF8).Grade);
        }

        [Fact]
        public void A_rule_can_ask_for_an_unhued_item()
        {
            var rule = new RareRule(null, null, null, HighlightGrade.Rare, null, hued: false);

            Assert.True(rule.Matches("a thing", 1, true, 0));
            Assert.False(rule.Matches("a thing", 1, true, 0x0455));
        }

        // ── Hats and mage bone armor (the 2009 page) ───────────────────────────────

        [Theory]
        [InlineData("bear mask", 0x1545, 0x0481, (int)HighlightGrade.Callout, "★ Polar Bear Hat")]
        [InlineData("deer mask", 0x1548, 0x047E, (int)HighlightGrade.Callout, "★ White Stag Mask")]
        [InlineData("bear mask", 0x1546, 0, (int)HighlightGrade.Prized, "Bear Hat")]
        [InlineData("deer mask", 0x1547, 0, (int)HighlightGrade.Prized, "Deer Hat")]
        [InlineData("magic bear mask", 0x1545, 0, (int)HighlightGrade.Prized, "Bear Hat")]
        public void Animal_hats_are_named_and_the_white_ones_called_out(string name, int graphic, int hue, int grade, string label)
        {
            var r = Graded(name, (ushort)graphic, (ushort)hue);

            Assert.Equal((HighlightGrade)grade, r.Grade);
            Assert.Equal(label, r.Label);
        }

        [Theory]
        [InlineData("bone armor", 0x144F, 0x0455)]
        [InlineData("bone helmet", 0x1451, 0x0497)]
        [InlineData("mage bone armor", 0x144F, 0)]
        public void Dark_bone_armor_is_mage_bone(string name, int graphic, int hue)
        {
            var r = Graded(name, (ushort)graphic, (ushort)hue);

            Assert.Equal(HighlightGrade.Callout, r.Grade);
            Assert.Equal("★ Mage Bone Armor", r.Label);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(0x08AC)]
        [InlineData(0x0845)]
        [InlineData(0x0851)]
        [InlineData(0x8851)]   // the partial-hue bit is masked off
        public void Plain_and_leather_bone_armor_is_nothing(int hue)
        {
            Assert.NotEqual(HighlightGrade.Callout, Graded("bone arms", 0x144E, (ushort)hue).Grade);
        }

        // ── Creatures ───────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("a black sheep", 0xCF, 0)]
        [InlineData("a sheep", 0xCF, 0x0455)]
        [InlineData("a sheep", 0xDF, 0x0001)]
        public void Black_sheep_is_a_callout(string name, int body, int hue)
        {
            Assert.True(AppraisalPalette.MatchMobile(name, (ushort)body, (ushort)hue, out HighlightGrade g, out string label));
            Assert.Equal(HighlightGrade.Callout, g);
            Assert.Equal("★ Black Sheep", label);
        }

        [Theory]
        [InlineData("a sheep", 0xCF, 0)]
        [InlineData("a sheep", 0xDF, 0)]
        [InlineData("a horse", 0xC8, 0)]
        [InlineData("a llama", 0xDC, 0x0455)]
        public void Ordinary_creatures_are_nothing(string name, int body, int hue)
        {
            Assert.False(AppraisalPalette.MatchMobile(name, (ushort)body, (ushort)hue, out _, out _));
        }

        // ── The Moonglow Zoo (census 2026-11-11) ───────────────────────────────────

        [Theory]
        [InlineData("a black wolf", 0x17, 0x076B, (int)HighlightGrade.Callout, "★ Black Wolf")]
        [InlineData("an arctic wolf", 0x25, 0x0481, (int)HighlightGrade.Callout, "★ Arctic Wolf")]
        [InlineData("a giant black scorpion", 0x30, 0x076B, (int)HighlightGrade.Callout, "★ Giant Black Scorpion")]
        [InlineData("", 0x1C, 0x076B, (int)HighlightGrade.Callout, "★ Rare Creature")]
        [InlineData("a war horse", 0xC8, 0x076B, (int)HighlightGrade.Callout, "★ War Horse")]
        [InlineData("an ancient gorilla", 0x1D, 0x0482, (int)HighlightGrade.Callout, "★ Ancient Gorilla")]
        [InlineData("a silver drake", 0x3D, 0x076D, (int)HighlightGrade.Callout, "★ Silver Drake")]
        [InlineData("a green drake", 0x3D, 0x08A4, (int)HighlightGrade.Callout, "★ Green Drake")]
        [InlineData("an albino giant rat", 0xD7, 0x047E, (int)HighlightGrade.Prized, "★ Albino Giant Rat")]
        [InlineData("a white hare", 0xCD, 0x047E, (int)HighlightGrade.Prized, "★ White Hare")]
        [InlineData("a white stag", 0xEA, 0x047E, (int)HighlightGrade.Prized, "★ White Stag")]
        [InlineData("a dire bear", 0xD4, 0x0452, (int)HighlightGrade.Prized, "★ Dire Bear")]
        [InlineData("a tropical bird", 0x06, 0x0556, (int)HighlightGrade.Prized, "★ Tropical Bird")]
        [InlineData("a tropical bird", 0x06, 0x0562, (int)HighlightGrade.Prized, "★ Tropical Bird")]
        public void Zoo_rares_are_called_out_by_their_own_name(string name, int body, int hue, int grade, string label)
        {
            Assert.True(AppraisalPalette.MatchMobile(name, (ushort)body, (ushort)hue, out HighlightGrade g, out string l));
            Assert.Equal((HighlightGrade)grade, g);
            Assert.Equal(label, l);
        }

        [Theory]
        [InlineData("a dire wolf", 0x17, 0)]
        [InlineData("a grey wolf", 0x1B, 0)]
        [InlineData("a timber wolf", 0xE1, 0)]
        [InlineData("a gorilla", 0x1D, 0)]
        [InlineData("a giant rat", 0xD7, 0)]
        [InlineData("a grizzly bear", 0xD4, 0)]
        [InlineData("a wild horse", 0xE4, 0)]
        [InlineData("a cougar", 0x3F, 0)]
        [InlineData("an eagle", 0x05, 0)]
        [InlineData("a rabbit", 0xCD, 0x0712)]
        [InlineData("a raven", 0x06, 0x0901)]
        [InlineData("a hawk", 0x06, 0x0836)]
        [InlineData("a sparrow", 0x06, 0x083F)]
        [InlineData("a thrush", 0x06, 0x0851)]
        [InlineData("a tropical bird", 0x06, 0x0845)]
        [InlineData("a baby ice serpent", 0x34, 0x0480)]
        public void Zoo_commons_are_nothing(string name, int body, int hue)
        {
            Assert.False(AppraisalPalette.MatchMobile(name, (ushort)body, (ushort)hue, out _, out _));
        }

        [Fact]
        public void A_hue_set_takes_values_and_ranges()
        {
            var set = new HueSet(0x0901).Range(0x0835, 0x0853);

            Assert.True(set.Contains(0x0901));
            Assert.True(set.Contains(0x0835));
            Assert.True(set.Contains(0x0853));
            Assert.False(set.Contains(0x0854));
            Assert.False(set.Contains(0x0556));
        }

        // ── Locked down ─────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("locked down", true)]
        [InlineData("locked down\nwriting table", true)]
        [InlineData("wooden chest\nLocked Down & Secure", true)]
        [InlineData("plant bowl", false)]
        [InlineData("a locked chest", false)]
        [InlineData("", false)]
        public void The_shards_label_says_locked_down(string text, bool locked)
        {
            Assert.Equal(locked, LockedDownLabels.Says(text));
        }

        // ── Town zones ──────────────────────────────────────────────────────────────

        // An L: the 10x10 square at the origin with its top-right 5x5 cut out.
        private static readonly int[] Xs = { 0, 5, 5, 10, 10, 0 };
        private static readonly int[] Ys = { 0, 0, 5, 5, 10, 10 };

        [Theory]
        [InlineData(0, 0, true)]
        [InlineData(4, 4, true)]
        [InlineData(9, 9, true)]
        [InlineData(5, 4, false)]   // the cut-out corner
        [InlineData(10, 5, false)]  // the corner rects are end-exclusive
        [InlineData(-1, 3, false)]
        public void Tiles_are_tested_by_their_centre(int x, int y, bool inside)
        {
            Assert.Equal(inside, TownZones.Inside(Xs, Ys, x + 0.5, y + 0.5));
        }
    }
}
