using AfterHour.Combat;
using static TestData;

public class TeamplayTests
{
    static bool Shift(Battle b, Combatant to) => b.Submit(new Command(CommandKind.ShiftChange, to));
    static bool CutIn(Battle b, Combatant who, Combatant? target = null) => b.Submit(new Command(CommandKind.CutIn, who, Target: target));

    /// <summary>Agenda Items are weak to everything; this makes them sturdy so chains can be tested.</summary>
    static void Sturdy(Combatant e, int stagger = 1000)
    {
        e.Body.Hp = e.Body.MaxHp = 9999;
        e.Body.Stagger = e.Body.MaxStagger = stagger;
    }

    [Fact]
    public void ShiftChangeReceiverActsNextThenHasFullTurnLength()
    {
        // Spec: the receiver acts next, then its countdown equals its full turn length.
        var b = NewBattle(["noa", "rook", "ines"], ["deadline"]);
        b.Run();                                       // Rook first
        var (noa, ines, dl) = (b.Party[0], b.Party[2], b.Enemies[0]);
        Act(b, "brace");
        Assert.Same(noa, b.Actor);
        Act(b, "noa_clock_in", dl);                    // weak to Physical
        Assert.Equal(BattlePhase.Reactions, b.Phase);
        Assert.Contains(ines, b.ShiftReceivers());
        Assert.True(Shift(b, ines));
        Assert.Equal(BattlePhase.ChooseAction, b.Phase);
        Assert.Same(ines, b.Actor);
        Act(b, "ines_sear", dl);                       // weak to Heat; Rook can still receive, so Reactions waits
        Assert.Equal(BattlePhase.Reactions, b.Phase);
        Assert.Equal(ines.TurnLength, ines.Countdown, 6);
    }

    [Fact]
    public void ChainBonus20PercentPerPass()
    {
        var b = NewBattle(["noa", "rook", "ines"], ["agenda_item"]);
        Exact(b);
        var events = Record(b);
        b.Run();                                       // Rook first
        var (noa, ines, item) = (b.Party[0], b.Party[2], b.Enemies[0]);
        Sturdy(item);
        Act(b, "rook_feedback", item);
        Assert.True(Shift(b, noa));                    // pass 1
        int from = events.Count;
        Act(b, "noa_clock_in", item);                  // 28 × 100/115 × 1.25 = 30.4, ×1.2 = 36.5 → 37
        Assert.Equal(37, events.Skip(from).First(e => e.Kind == EventKind.Damage).Amount);
        Assert.True(Shift(b, ines));                   // pass 2
        from = events.Count;
        Act(b, "ines_sear", item);                     // 32 × 100/115 × 1.25 = 34.8, ×1.4 = 48.7 → 49
        Assert.Equal(49, events.Skip(from).First(e => e.Kind == EventKind.Damage).Amount);
    }

    [Fact]
    public void CannotPassBackWithinChain()
    {
        var b = NewBattle(["noa", "rook", "ines"], ["agenda_item"]);
        b.Run();
        var (noa, rook, ines, item) = (b.Party[0], b.Party[1], b.Party[2], b.Enemies[0]);
        Sturdy(item);
        Act(b, "rook_feedback", item);
        Assert.True(Shift(b, noa));
        Act(b, "noa_clock_in", item);
        Assert.Equal(new[] { ines }, b.ShiftReceivers());
        Assert.False(Shift(b, rook));
        Assert.Equal(BattlePhase.Reactions, b.Phase);
    }

    [Fact]
    public void ShiftNeedsRank2ExceptNoa()
    {
        var b = NewBattle(["noa", "rook"], ["agenda_item"]);
        b.Run();                                       // Rook first
        var (noa, rook, item) = (b.Party[0], b.Party[1], b.Enemies[0]);
        Sturdy(item);
        rook.BondRank = 1;
        Act(b, "rook_feedback", item);                 // weakness, but rank 1 can't pass: no Reactions
        Assert.Same(noa, b.Actor);
        Assert.Equal(BattlePhase.ChooseAction, b.Phase);
        Act(b, "noa_clock_in", item);                  // Noa has no rank and can always pass
        Assert.Equal(BattlePhase.Reactions, b.Phase);
        Assert.Equal(new[] { rook }, b.ShiftReceivers());
    }

    [Theory]
    [InlineData(3, 1, 0)]
    [InlineData(4, 1, 30)]
    public void Rank3GivesMinuteRank4Heals10(int rank, int extraMinutes, int heal)
    {
        var b = NewBattle(["noa", "rook"], ["agenda_item"]);
        b.Run();                                       // Rook first: the passer
        var (noa, rook, item) = (b.Party[0], b.Party[1], b.Enemies[0]);
        Sturdy(item);
        rook.BondRank = rank;
        noa.Body.Hp = 200;
        Act(b, "rook_feedback", item);                 // +1 Minute from the basic attack → 4
        var minutes = b.Minutes;
        Assert.True(Shift(b, noa));
        Assert.Equal(minutes + extraMinutes, b.Minutes);
        Assert.Equal(200 + heal, noa.Body.Hp);         // 10% of 300 at rank 4
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(3, 20)]
    public void CrowdWorkGivesRook20OnReceive(int rank, int bonus)
    {
        var b = NewBattle(["noa", "rook"], ["agenda_item"]);
        b.Run();
        var (noa, rook, item) = (b.Party[0], b.Party[1], b.Enemies[0]);
        Sturdy(item);
        rook.BondRank = rank;
        Act(b, "brace");                               // Rook: +15 for acting
        Assert.Same(noa, b.Actor);
        Act(b, "noa_clock_in", item);
        var before = rook.Overtime;
        Assert.True(Shift(b, rook));
        Assert.Equal(before + bonus, rook.Overtime);
    }

    [Fact]
    public void OvertimeGainsAndCaps()
    {
        // Acting: +15.
        var a = NewBattle(["noa"], ["deadline"]);
        a.Run();
        Act(a, "brace");
        Assert.Equal(15, a.Party[0].Overtime);

        // A weakness hit: +15 once per action, even when the AoE hits two weak targets.
        var b = NewBattle(["rook"], ["agenda_item", "agenda_item"]);
        var events = Record(b);
        b.Run();                                       // Rook first
        var rook = b.Party[0];
        Act(b, "rook_drop_the_bass");
        int act = events.FindIndex(e => e.Kind == EventKind.Action && e.Target == rook);
        var gained = events.Skip(act + 1).TakeWhile(e => e.Kind != EventKind.Action)
            .Where(e => e.Kind == EventKind.Overtime && e.Target == rook).Sum(e => e.Amount);
        Assert.Equal(30, gained);

        // Hits taken: +5 each, at most +15 per enemy action. Jostle is 3 hits.
        var c = NewBattle(["bram"], ["rush_hour"]);
        c.Run();                                       // Rush Hour first
        Assert.Equal(15, c.Party[0].Overtime);

        // The gauge caps at 100.
        var d = NewBattle(["noa"], ["deadline"]);
        d.Run();
        d.Party[0].Overtime = 95;
        Act(d, "brace");
        Assert.Equal(100, d.Party[0].Overtime);

        // Fatigued drains at turn end and never goes below 0.
        var e = NewBattle(["noa"], ["deadline"]);
        e.Data.Tuning.FatiguedOt = 50;
        e.Run();
        e.Party[0].Statuses.Add(new StatusInstance { Id = StatusId.Fatigued, TurnsLeft = 2 });
        Act(e, "brace");                               // 0 + 15 − 50 → 0
        Assert.Equal(0, e.Party[0].Overtime);
    }

    [Fact]
    public void CutInOnlyBeforeEnemyTurnsWhenGaugeFull()
    {
        var b = NewBattle(["noa"], ["deadline"]);
        b.Run();
        var (noa, dl) = (b.Party[0], b.Enemies[0]);
        Act(b, "brace");                               // the Deadline acts in between: no window without a full gauge
        Assert.Equal(BattlePhase.ChooseAction, b.Phase);

        noa.Overtime = 100;
        dl.Countdown = 0;                              // the Deadline is next, not Noa
        Act(b, "brace");
        Assert.Equal(BattlePhase.TurnStart, b.Phase);  // the window opens before the Deadline's turn
        Assert.Same(dl, b.Actor);
        Assert.True(b.Submit(new Command(CommandKind.Pass)));
        Assert.Equal(BattlePhase.ChooseAction, b.Phase);
        Assert.Same(noa, b.Actor);
    }

    [Fact]
    public void StopTheClockEncoreKitchenFire()
    {
        var b = NewBattle(["noa", "rook", "ines"], ["deadline"]);
        Exact(b);
        var events = Record(b);
        b.Run();                                       // Rook's turn; everyone cuts in during it
        var (noa, rook, ines, dl) = (b.Party[0], b.Party[1], b.Party[2], b.Enemies[0]);

        noa.Overtime = 100;
        var noaCountdown = noa.Countdown;
        int from = events.Count;
        Assert.True(CutIn(b, noa));                    // Stop the Clock: enemies +40%, +2 Minutes
        Assert.Equal(0.4 * dl.TurnLength, events.Skip(from).Single(e => e.Kind == EventKind.Delay && e.Target == dl).Amount, 6);
        Assert.Equal(5, b.Minutes);
        Assert.Equal(noaCountdown, noa.Countdown);
        Assert.Equal(0, noa.Overtime);
        Assert.Equal(BattlePhase.ChooseAction, b.Phase);
        Assert.Same(rook, b.Actor);

        b.Minutes = 3;
        rook.Overtime = 100;
        ines.Countdown = 300;
        from = events.Count;
        Assert.True(CutIn(b, rook));                   // Encore: every ally −50% of their turn length, +1 Minute
        Assert.Equal(50, events.Skip(from).Single(e => e.Kind == EventKind.Advance && e.Target == ines).Amount, 6);
        Assert.Equal(250, ines.Countdown, 6);
        Assert.Equal(4, b.Minutes);
        Assert.Equal(0, rook.Countdown);               // advanced like everyone, never reset to a recovery

        ines.Overtime = 100;
        from = events.Count;
        Assert.True(CutIn(b, ines, dl));               // 4.0 × 32 × 100/(100 + 20×0.5) × 1.25 = 145.5 → 145
        Assert.Equal(145, events.Skip(from).First(e => e.Kind == EventKind.Damage && e.Target == dl).Amount);
        Assert.Equal(250, ines.Countdown, 6);
        Assert.False(CutIn(b, ines, dl));              // the gauge is spent
    }

    [Fact]
    public void RushOfferedOnlyWhenThisActionStaggersTheLastStanding()
    {
        var b = NewBattle(["noa", "rook", "ines"], ["agenda_item", "agenda_item"]);
        b.Run();                                       // Rook first
        var (noa, i1, i2) = (b.Party[0], b.Enemies[0], b.Enemies[1]);
        Sturdy(i1, stagger: 10);
        Sturdy(i2, stagger: 10);
        Act(b, "rook_feedback", i1);                   // breaks the first; the second still stands
        Assert.Equal(BattlePhase.Reactions, b.Phase);
        Assert.False(b.RushReady);
        Assert.True(Shift(b, noa));
        Act(b, "noa_clock_in", i2);                    // breaks the last one standing
        Assert.True(b.RushReady);

        // Everyone already Staggered and this hit breaks nothing new: no Rush.
        var c = NewBattle(["noa", "rook", "ines"], ["agenda_item", "agenda_item"]);
        c.Run();
        foreach (var e in c.Enemies)
        {
            Sturdy(e, stagger: 0);
            e.Body.Staggered = true;
        }
        Act(c, "rook_feedback", c.Enemies[0]);
        Assert.Equal(BattlePhase.Reactions, c.Phase);  // a weakness hit still offers Shift Change
        Assert.False(c.RushReady);
    }

    [Fact]
    public void RushHitsEachEnemyOncePerMember()
    {
        var b = NewBattle(["noa", "rook", "ines"], ["agenda_item", "agenda_item"]);
        Exact(b);
        var events = Record(b);
        b.Run();
        var (noa, i1, i2) = (b.Party[0], b.Enemies[0], b.Enemies[1]);
        Sturdy(i1, stagger: 10);
        Sturdy(i2, stagger: 10);
        Act(b, "rook_feedback", i1);
        Assert.True(Shift(b, noa));
        Act(b, "noa_clock_in", i2);
        int from = events.Count;
        Assert.True(b.Submit(new Command(CommandKind.Rush)));
        // Power 1.2, no element, ×1.5 on Staggered, DEF 15: Noa 28 → 43.8 → 44, Rook 24 → 37.6 → 38, Ines 32 → 50.1 → 50.
        foreach (var item in new[] { i1, i2 })
        {
            var hits = events.Skip(from).Where(e => e.Kind == EventKind.Damage && e.Target == item).Select(e => e.Amount).OrderBy(x => x);
            Assert.Equal(new[] { 38.0, 44.0, 50.0 }, hits);
        }
        Assert.NotEqual(BattlePhase.Reactions, b.Phase);
    }
}
