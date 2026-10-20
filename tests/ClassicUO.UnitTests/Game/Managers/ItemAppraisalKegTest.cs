using ClassicUO.Game.Managers;
using Xunit;

namespace ClassicUO.UnitTests.Game.Managers
{
    public class ItemAppraisalKegTest
    {
        private static ItemAppraisal.Result Keg(string text)
        {
            var r = new ItemAppraisal.Result();
            ItemAppraisal.ParseKeg(text, ref r);
            return r;
        }

        [Fact]
        public void Label_lines_win_over_the_stale_empty_name()
        {
            // Captured live: the OPL name still says empty, the labels say otherwise.
            var r = Keg("An empty potion keg\nA keg of Greater Explosion potions\nThe keg is very full.");

            Assert.Equal("explosion", r.KegPotion);
            Assert.Equal(0.88f, r.KegLevel);
        }

        [Fact]
        public void Empty_keg_has_no_level()
        {
            var r = Keg("An empty potion keg\nThe keg is empty.");

            Assert.Equal("unknown", r.KegPotion);
            Assert.Equal(0f, r.KegLevel);
        }

        [Fact]
        public void Keg_before_its_labels_arrive_has_no_level()
        {
            Assert.Equal(0f, Keg("An empty potion keg").KegLevel);
        }

        [Theory]
        [InlineData("A keg of Total Refreshment potions", "refresh")]
        [InlineData("A keg of Greater Heal potions", "heal")]
        [InlineData("A keg of Lesser Cure potions", "cure")]
        [InlineData("A keg of Deadly Poison potions", "poison")]
        [InlineData("A keg of Nightsight potions", "nightsight")]
        [InlineData("A keg of yellow liquid.", "heal")]
        [InlineData("A keg of black liquid.", "nightsight")]
        [InlineData("A keg of strange liquid.", "unknown")]
        public void Family_from_potion_or_liquid_name(string name, string family)
        {
            Assert.Equal(family, Keg(name + "\nThe keg is about one third full.").KegPotion);
        }

        [Theory]
        [InlineData("The keg is nearly empty.", 0.05f)]
        [InlineData("The keg is almost half full.", 0.43f)]
        [InlineData("The keg is approximately half full.", 0.5f)]
        [InlineData("The keg is more than half full.", 0.62f)]
        [InlineData("The liquid is almost to the top of the keg.", 0.97f)]
        [InlineData("The keg is completely full.", 1f)]
        public void Fill_line_sets_level(string line, float level)
        {
            Assert.Equal(level, Keg("A keg of Heal potions\n" + line).KegLevel);
        }

        [Fact]
        public void Storage_keg_is_not_a_potion_keg()
        {
            Assert.Null(Keg("locked down\nkeg\n(0 items, 0 stones)").KegPotion);
        }
    }
}
