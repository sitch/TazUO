using ClassicUO.Game.Managers;
using Xunit;

namespace ClassicUO.UnitTests.Game.Managers
{
    public class ItemAppraisalGradeTest
    {
        private static ItemAppraisal.Result Seed(string line)
        {
            var r = new ItemAppraisal.Result();
            ItemAppraisal.ParseSeed(line, ref r);
            return r;
        }

        private static ItemAppraisal.Result Bonuses(string text)
        {
            var r = new ItemAppraisal.Result();
            ItemAppraisal.ParseBonuses(text.Split('\n'), ref r);
            return r;
        }

        // Name is set by the cache from the OPL (name + first data line), not by Parse.
        private static ItemAppraisal.Result Graded(string text)
        {
            var r = ItemAppraisal.Parse(text);
            string[] lines = text.Split('\n');
            r.Name = lines.Length > 1 ? lines[0] + "\n" + lines[1] : lines[0];
            return ItemAppraisal.Recolor(r);
        }

        // ── Seeds ───────────────────────────────────────────────────────────────────

        [Fact]
        public void Typed_seed_reads_colour_tier_and_plant_index()
        {
            var r = Seed("bright red ponytail palm seed");

            Assert.Equal("bright red", r.SeedColor);
            Assert.Equal(2, r.SeedTier);
            Assert.Equal(10, r.PlantType);
        }

        [Fact]
        public void Longest_colour_wins_over_its_suffix()
        {
            Assert.Equal("fire red", Seed("fire red seed").SeedColor);
            Assert.Equal("red", Seed("a red seed").SeedColor);
        }

        [Theory]
        [InlineData("3 plain seeds")]
        [InlineData("a plain seed : 3")]
        [InlineData("Plain Seed")]
        public void Amounts_articles_and_case_are_ignored(string line)
        {
            var r = Seed(line);

            Assert.Equal("plain", r.SeedColor);
            Assert.Equal(0, r.PlantType);
        }

        [Fact]
        public void Category_seed_is_a_bonsai()
        {
            var r = Seed("common seed");

            Assert.Equal("common", r.Bonsai);
            Assert.Null(r.SeedColor);
        }

        [Theory]
        [InlineData("marijuana seeds")]
        [InlineData("a fishing weight")]
        [InlineData("red")]
        public void Other_names_are_not_seeds(string line)
        {
            var r = Seed(line);

            Assert.Null(r.SeedColor);
            Assert.Null(r.Bonsai);
        }

        [Fact]
        public void Locked_down_label_does_not_hide_the_seed()
        {
            var r = ItemAppraisal.Parse("[locked down]\nbright purple barrel cactus seed");

            Assert.Equal("bright purple", r.SeedColor);
            Assert.Equal(16, r.PlantType);
        }

        // ── Bonuses ─────────────────────────────────────────────────────────────────

        [Fact]
        public void Stat_bonus_reads_as_short_stat()
        {
            var r = Bonuses("a gold ring\nIntelligence Bonus 4");

            Assert.Equal("int", r.BonusKey);
            Assert.Equal("+4 Int", r.BonusLabel);
            Assert.Equal("+4 Int", r.BonusShort);
        }

        [Theory]
        [InlineData("Tailoring +5", "+5 Tailoring", "+5 Tail.")]
        [InlineData("Magic Resistance +5", "+5 Magic Resistance", "+5 MR")]
        [InlineData("Mining Bonus +1", "+1 Mining", "+1 Mining")]
        public void Skill_bonus_reads_with_a_cell_sized_short(string line, string label, string shortName)
        {
            var r = Bonuses(line);

            Assert.Equal("skill", r.BonusKey);
            Assert.Equal(label, r.BonusLabel);
            Assert.Equal(shortName, r.BonusShort);
        }

        [Fact]
        public void Two_bonuses_join_in_the_label_and_the_first_keeps_the_badge()
        {
            var r = Bonuses("Strength Bonus 2\nHiding +5");

            Assert.Equal("+2 Str · +5 Hiding", r.BonusLabel);
            Assert.Equal("+2 Str", r.BonusShort);
            Assert.Equal("str", r.BonusKey);
        }

        [Theory]
        [InlineData("[+5 through +10 Damage Bonus]")]
        [InlineData("Weight +5")]
        [InlineData("Intelligence Bonus 0")]
        public void Non_skill_plus_text_is_not_a_bonus(string line)
        {
            Assert.Null(Bonuses(line).BonusKey);
        }

        [Fact]
        public void Blessed_line_is_noted()
        {
            Assert.True(Bonuses("a sash\nBlessed").Blessed);
        }

        // ── Tried on (LegionScripts/.appraise.txt) ─────────────────────────────────

        private const string TriedOn = @"{
          ""1073741901"": {""name"": ""really really magic wizards hat"", ""gives"": {""Forensic Evaluation"": 5}, ""sure"": true},
          ""1073741902"": {""name"": ""an antique gold necklace"", ""gives"": {""Int"": 2}},
          ""1073741903"": {""name"": ""a fancy shirt"", ""gives"": {}, ""capped"": [""Hiding""]},
          ""1073741904"": {""name"": ""magic skull cap"", ""gives"": {""Dex"": 3, ""Str"": 1}, ""sure"": false},
          ""1073741905"": {""name"": ""magic cloak"", ""gives"": {""Phys"": 2, ""Bowcraft/Fletching"": 5.0}},
          ""1073741906"": {""name"": ""a sash"", ""refused"": true}
        }";

        [Fact]
        public void Tried_on_file_becomes_the_shards_bonus_lines()
        {
            var lines = AppraisalTriedOn.Parse(TriedOn);

            Assert.Equal(new[] { "Forensic Evaluation +5" }, lines[1073741901]);
            Assert.Equal(new[] { "Intelligence Bonus 2" }, lines[1073741902]);
            Assert.Equal(new[] { "Bowcraft/Fletching +5" }, lines[1073741905]);
        }

        [Fact]
        public void Nothing_unsure_or_refused_is_labelled()
        {
            var lines = AppraisalTriedOn.Parse(TriedOn);

            Assert.False(lines.ContainsKey(1073741903));
            Assert.False(lines.ContainsKey(1073741904));
            Assert.False(lines.ContainsKey(1073741906));
        }

        [Fact]
        public void Tried_on_hat_is_prized_and_labelled_with_what_it_gave()
        {
            var r = ItemAppraisal.Parse("really really magic wizards hat");
            r.Name = "really really magic wizards hat";
            ItemAppraisal.ParseBonuses(AppraisalTriedOn.Parse(TriedOn)[1073741901], ref r);
            r = ItemAppraisal.Recolor(r);

            Assert.Equal(HighlightGrade.Prized, r.Grade);
            Assert.Equal("+5 Forensic Evaluation", r.Label);
            Assert.Equal("skill", r.BonusKey);
        }

        [Fact]
        public void Slash_skill_reads_as_a_bonus()
        {
            Assert.Equal("+5 Bowcraft/Fletching", Bonuses("Bowcraft/Fletching +5").BonusLabel);
        }

        // ── Grades ──────────────────────────────────────────────────────────────────

        [Fact]
        public void Magical_wizards_hat_is_a_trace()
        {
            var r = Graded("A Magical Wizard's Hat\nBlessed\nIntelligence Bonus 1");

            Assert.Equal(HighlightGrade.Trace, r.Grade);
        }

        [Fact]
        public void Really_really_magic_hat_is_magic_clothing()
        {
            Assert.Equal(HighlightGrade.Prized, Graded("a really really magic wizards hat").Grade);
        }

        [Fact]
        public void Magic_clothing_is_prized_and_labelled_with_its_bonus()
        {
            var r = Graded("Magic Surcoat\nBlessed\nDexterity Bonus 5");

            Assert.Equal(HighlightGrade.Prized, r.Grade);
            Assert.Equal("+5 Dex", r.Label);
            Assert.Equal("+5 Dex", r.Badge);
        }

        [Fact]
        public void Blessed_bonus_is_prized_and_bare_bonus_a_trace()
        {
            Assert.Equal(HighlightGrade.Prized, Graded("a sash\nBlessed\nHiding +5").Grade);
            Assert.Equal(HighlightGrade.Trace, Graded("mining gloves\nMining Bonus +5").Grade);
        }

        [Fact]
        public void Typed_seed_steps_up_a_grade_and_labels_its_index()
        {
            var r = Graded("bright red ponytail palm seed");

            Assert.Equal(HighlightGrade.Prized, r.Grade);
            Assert.Equal("Bright Red · 10/17 Ponytail Palm", r.Label);
            Assert.Equal("10", r.Badge);
        }

        [Fact]
        public void Common_seed_is_a_bonsai_callout()
        {
            var r = Graded("common seed");

            Assert.Equal(HighlightGrade.Callout, r.Grade);
            Assert.Equal("★", r.Badge);
        }

        [Fact]
        public void Exceptional_alone_takes_no_tier_border()
        {
            var r = Graded("a longsword\nExceptional");

            Assert.True(r.Exceptional);
            Assert.Null(r.Outline);
            Assert.False(r.Rare);
        }

        [Fact]
        public void Crate_contents_are_rare_but_not_the_bags()
        {
            var r = ItemAppraisal.Parse("a spool of thread");
            r.RareContainer = HighlightGrade.Rare;
            Assert.Equal(HighlightGrade.Rare, ItemAppraisal.Recolor(r).Grade);

            r.IsContainer = true;
            Assert.Equal(HighlightGrade.None, ItemAppraisal.Recolor(r).Grade);
        }

        [Fact]
        public void Tray_is_rare_only_off_the_ground()
        {
            var r = ItemAppraisal.Parse("a baking tray");
            r.Graphic = 0x0991;
            Assert.Equal(HighlightGrade.Rare, ItemAppraisal.Recolor(r).Grade);

            r.OnGround = true;
            Assert.Equal(HighlightGrade.None, ItemAppraisal.Recolor(r).Grade);
        }

        [Fact]
        public void Display_name_skips_the_locked_down_line_and_the_amount()
        {
            Assert.Equal("Antique Vase", ItemAppraisal.DisplayName("[locked down]\nantique vase : 2"));
        }
    }
}
