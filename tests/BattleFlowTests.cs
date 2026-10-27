using AfterHour.Combat;
using static TestData;

public class BattleFlowTests
{
    /// <summary>Each unit's countdown when the battle began, read at the first TurnStart (before anyone acts).</summary>
    static Dictionary<Combatant, double> StartCountdowns(Battle b)
    {
        Dictionary<Combatant, double>? snap = null;
        b.Emitted += e =>
        {
            if (e.Kind == EventKind.TurnStart && snap == null)
                snap = b.Party.Concat(b.Enemies).ToDictionary(u => u, u => u.Countdown + b.Clock);
        };
        b.Run();
        return snap!;
    }

    [Fact]
    public void BattleStartSetsCountdownsMinutesAndIntents()
    {
        var b = NewBattle(["noa", "rook", "ines"], ["deadline", "hold_music", "insomnia"]);
        var start = StartCountdowns(b);
        Assert.All(start, kv => Assert.Equal(kv.Key.TurnLength, kv.Value, 6));
        Assert.Equal(3, b.Minutes);
        Assert.All(b.Enemies, e => Assert.NotNull(e.Intent));
    }

    [Theory]
    [InlineData(StartMode.PreEmptive, 1.3, 1.0, 4)]
    [InlineData(StartMode.Ambushed, 1.0, 1.3, 3)]
    public void PreEmptiveAndAmbush(StartMode mode, double enemyFactor, double partyFactor, int minutes)
    {
        var b = NewBattle(["noa", "rook", "ines"], ["deadline", "hold_music"], mode);
        var start = StartCountdowns(b);
        foreach (var (u, c) in start)
            Assert.Equal(u.TurnLength * (u.IsParty ? partyFactor : enemyFactor), c, 6);
        Assert.Equal(minutes, b.Minutes);
    }

    [Fact]
    public void MinutesClampAndSkillsNeedMinutes()
    {
        var b = NewBattle(["noa"], ["deadline"]);   // Noa (SPD 105) acts before Deadline (SPD 80)
        b.Run();
        var deadline = b.Enemies[0];
        b.Minutes = 5;
        Act(b, "noa_clock_in", deadline);           // +1 Minute, but the pool holds 5
        Assert.Equal(5, b.Minutes);

        b.Minutes = 0;
        var phase = b.Phase;
        var actor = b.Actor;
        Assert.False(b.Submit(new Command(CommandKind.Act, actor, b.Data.ActionById("noa_skip"), deadline)));
        Assert.Equal(phase, b.Phase);
        Assert.Same(actor, b.Actor);
        Assert.Equal(0, b.Minutes);
        Assert.False(b.CanUse(actor!, b.Data.ActionById("noa_skip"), out var why));
        Assert.Contains("Minute", why);
    }

    [Fact]
    public void WeaknessHitDrainsStaggerAndDelays10Percent()
    {
        var b = NewBattle(["noa"], ["deadline"]);
        var events = Record(b);
        b.Run();
        var deadline = b.Enemies[0];
        Act(b, "noa_clock_in", deadline);
        Assert.Equal(30, deadline.Body.Stagger);
        var delay = Assert.Single(events, e => e.Kind == EventKind.Delay && e.Target == deadline);
        Assert.Equal(12.5, delay.Amount, 6);   // 10% of 10000/80
        Assert.Contains("deadline:Physical", b.Known);
    }

    [Fact]
    public void DelayCapIs30Percent()
    {
        // Spec: five weakness hits on one enemy between its turns delay it 30% in total, not 50%.
        var b = NewBattle(["noa"], ["deadline"]);
        var events = Record(b);
        b.Run();
        var deadline = b.Enemies[0];
        deadline.Body.Stagger = deadline.Body.MaxStagger = 1000;
        deadline.Countdown = 5000;
        for (int i = 0; i < 5; i++) Act(b, "noa_clock_in", deadline);
        var total = events.Where(e => e.Kind == EventKind.Delay && e.Target == deadline).Sum(e => e.Amount);
        Assert.Equal(37.5, total, 6);
    }

    [Fact]
    public void StaggerBreakDelaysAndCostsNextTurn()
    {
        var b = NewBattle(["rook"], ["rush_hour"]);   // Rush Hour (SPD 120) acts first, then Rook
        Exact(b);
        var events = Record(b);
        Intent? intentAtBreak = null;
        b.Emitted += e => { if (e.Kind == EventKind.StaggerBreak) intentAtBreak = e.Target!.Intent?.Intent; };
        b.Run();
        var rh = b.Enemies[0];
        rh.Body.Stagger = 10;                          // one Feedback (Stagger 10, Resonance = weak) breaks it
        Act(b, "rook_feedback", rh);

        int brk = events.FindIndex(e => e.Kind == EventKind.StaggerBreak && e.Target == rh);
        Assert.True(brk >= 0);
        Assert.Contains(events, e => e.Kind == EventKind.Delay && e.Target == rh && Math.Abs(e.Amount - 0.25 * rh.TurnLength) < 1e-6);
        Assert.Equal(Intent.Recover, intentAtBreak);

        for (int i = 0; i < 3 && !events.Any(e => e.Kind == EventKind.Recover); i++) Act(b, "brace");
        int rec = events.FindIndex(e => e.Kind == EventKind.Recover && e.Target == rh);
        Assert.True(rec > brk);
        Assert.DoesNotContain(events.Skip(brk).Take(rec - brk + 1), e => e.Kind == EventKind.Damage && e.Target!.IsParty);
        Assert.False(rh.Body.Staggered);
        Assert.Equal(30, rh.Body.Stagger);
    }

    [Fact]
    public void BraceGuardsAndIsLight()
    {
        var b = NewBattle(["noa"], ["agenda_item"]);  // Noa acts at 95.2, the Agenda Item at 100
        Exact(b);
        var events = Record(b);
        b.Run();
        var noa = b.Party[0];
        var clock1 = b.Clock;
        Act(b, "brace");                               // Guard, +1 Minute, Light recovery
        Assert.Equal(4, b.Minutes);
        Assert.Equal(0.75 * noa.TurnLength, b.Clock - clock1, 6);   // 71.4
        var hit = Assert.Single(events, e => e.Kind == EventKind.Damage && e.Target == noa);
        Assert.Equal(11, hit.Amount);                  // 22 × 100/120 = 18.3, guarded ×0.6 = 11
        Assert.False(noa.Has(StatusId.Guard));         // Guard ends when her next turn starts
    }

    [Fact]
    public void KillingLastEnemyWithAoeEndsBattle()
    {
        var b = NewBattle(["ines"], ["hold_music", "insomnia"]);
        var events = Record(b);
        b.Run();
        foreach (var e in b.Enemies) e.Body.Hp = 1;
        b.Minutes = 5;
        Act(b, "ines_grease_fire");
        Assert.Equal(BattlePhase.BattleEnd, b.Phase);
        Assert.True(b.Won);
        Assert.Equal(EventKind.BattleEnd, events[^1].Kind);
        int act = events.FindLastIndex(e => e.Kind == EventKind.Action);
        Assert.DoesNotContain(events.Skip(act), e => e.Kind == EventKind.TurnStart);
    }

    [Fact]
    public void PauseAfterEachTurnLetsTheScreenCatchUp()
    {
        var b = NewBattle(["noa"], ["rush_hour"]);    // Rush Hour acts first
        var events = Record(b);
        b.PauseAfterEachTurn = true;
        b.Run();                                       // battle start: countdowns set, intents rolled, nobody has acted
        Assert.Equal(BattlePhase.TurnStart, b.Phase);
        Assert.False(b.AwaitingInput);
        Assert.DoesNotContain(events, e => e.Kind == EventKind.TurnStart);
        Assert.NotNull(b.Enemies[0].Intent);
        b.Run();
        Assert.Equal(BattlePhase.TurnStart, b.Phase); // stopped after Rush Hour's turn, not waiting on the player
        Assert.False(b.AwaitingInput);
        Assert.Single(events, e => e.Kind == EventKind.TurnStart);
        b.Run();
        Assert.Equal(BattlePhase.ChooseAction, b.Phase);
        Assert.True(b.AwaitingInput);

        b.Party[0].Overtime = 100;                     // a full gauge opens the cut-in window before Rush Hour's turn
        b.Enemies[0].Countdown = 0;
        Act(b, "brace");
        while (!b.AwaitingInput) b.Run();
        Assert.Equal(BattlePhase.TurnStart, b.Phase);
        Assert.Same(b.Enemies[0], b.Actor);
    }

    [Fact]
    public void AllPartyKoIsDefeat()
    {
        var b = NewBattle(["noa"], ["rush_hour"]);    // Rush Hour acts first: Jostle, 3 hits
        b.Party[0].Body.Hp = 1;
        b.Run();
        Assert.Equal(BattlePhase.BattleEnd, b.Phase);
        Assert.False(b.Won);
        Assert.False(b.Party[0].Alive);
    }
}
