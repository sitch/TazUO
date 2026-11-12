using ClassicUO.Game.Managers;
using Xunit;

namespace ClassicUO.UnitTests.Game.Managers
{
    public class AppraisalRulesTest
    {
        private static ItemAppraisal.Result Graded(string text, ushort graphic, ushort hue = 0, bool onGround = true)
        {
            var r = ItemAppraisal.Parse(text);
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
        [InlineData("a horse", 0xC8, 0x0455)]
        public void Ordinary_creatures_are_nothing(string name, int body, int hue)
        {
            Assert.False(AppraisalPalette.MatchMobile(name, (ushort)body, (ushort)hue, out _, out _));
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
