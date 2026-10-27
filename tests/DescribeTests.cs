using System.Globalization;
using AfterHour.Combat;

public class DescribeTests
{
    static readonly CombatData D = TestData.Load();

    static string Say(string id, bool enemy = false) => Describe.Action(D.ActionById(id), D, enemy);

    [Theory]
    [InlineData("noa_clock_in", "Physical, Power 1.0, Stagger 5 to one enemy. +1 Minute.")]
    [InlineData("ines_flambe", "Heat, Power 2.2, Stagger 25 to one enemy. Burn on one enemy (2 turns): loses 8% of max HP (at most 40) at the start of each turn.")]
    [InlineData("noa_skip", "Delays one enemy by 25% of a turn. Can't target the same enemy again before it acts. Light: your next turn comes sooner.")]
    [InlineData("noa_fast_forward", "Pulls one ally's next turn to the front. Light: your next turn comes sooner.")]
    [InlineData("rook_hype_up", "Focus on one ally (2 turns): deals 25% more damage. Moves one ally up by 30% of a turn.")]
    [InlineData("rook_drop_the_bass", "Resonance, Power 0.8, Stagger 10 to all enemies. Echo on all enemies (until triggered): the next hit taken repeats at 50%. Heavy: your next turn comes later.")]
    [InlineData("ines_double_shift", "Act again right away. Costs 20% of max HP. Once every 3 turns.")]
    [InlineData("ines_kitchen_fire", "Heat, Power 4.0, Stagger 40 to one enemy. Ignores 50% of DEF. Doesn't use a turn.")]
    [InlineData("noa_stop_the_clock", "Delays all enemies by 40% of a turn. The party gains 2 Minutes. Doesn't use a turn.")]
    [InlineData("bram_paint_over", "Shields one ally for 20% of the user's max HP. Light: your next turn comes sooner.")]
    [InlineData("bram_restoration", "Heals every ally for 40% of max HP. Removes debuffs from every ally. Doesn't use a turn.")]
    [InlineData("brace", "Guard on yourself (until the next turn): takes 40% less damage. +1 Minute. Light: your next turn comes sooner.")]
    [InlineData("item_coffee", "The party gains 2 Minutes.")]
    [InlineData("item_painkillers", "Removes all statuses from one ally.")]
    public void DescribesPartyActions(string id, string expected) => Assert.Equal(expected, Say(id));

    [Theory]
    [InlineData("rush_hour_jostle", "Physical, Power 0.4 × 3 hits to one party member.")]
    [InlineData("hold_music_please_hold", "Physical, Power 0.6 to one party member. Delays one party member by 25% of a turn.")]
    [InlineData("deadline_final_notice", "Physical, Power 2.5 to the whole party. Heavy: its next turn comes later.")]
    [InlineData("deadline_tick", "Counts down to its finisher.")]
    [InlineData("rush_hour_stall", "Does nothing. Heavy: its next turn comes later.")]
    [InlineData("insomnia_steal", "Physical, Power 0.6 to one party member. Steals 1 Minute from the party.")]
    [InlineData("endless_meeting_summon", "Calls in an Agenda Item (up to 2).")]
    [InlineData("boss_unpaid_hours", "Steals 1 Minute from the party. Heals itself for 5% of max HP.")]
    public void DescribesEnemyMoves(string id, string expected) => Assert.Equal(expected, Say(id, enemy: true));

    [Fact]
    public void DescriptionsFollowTheLiveNumbers()
    {
        var d = TestData.Load();
        d.Tuning.BurnPct = 0.1;
        var flambe = d.ActionById("ines_flambe") with { Power = 2.5 };
        var text = Describe.Action(flambe, d);
        Assert.Contains("Power 2.5", text);
        Assert.Contains("loses 10% of max HP", text);
    }

    [Fact]
    public void NumbersIgnoreTheSystemLanguage()
    {
        // Indonesian Windows writes 2,2 and "20 %"; the game should always say 2.2 and 20%.
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("id-ID");
            Assert.Contains("Power 2.2", Say("ines_flambe"));
            Assert.Contains("Costs 20% of max HP", Say("ines_double_shift"));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void EveryActionAndPassiveHasADescription()
    {
        foreach (var a in D.Actions)
            Assert.False(string.IsNullOrWhiteSpace(Describe.Action(a, D)), a.Id);
        foreach (var p in D.Combatants.SelectMany(c => c.Passives ?? []))
            Assert.DoesNotContain(p, Describe.Passive(p, D.Tuning));   // a real sentence, not the raw id
        Assert.Equal("Line Cook: Heat skills deal 20% more damage to Burning enemies.", Describe.Passive("line_cook", D.Tuning));
    }
}
