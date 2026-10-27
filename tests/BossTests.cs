using AfterHour.Combat;
using static TestData;

public class BossTests
{
    static readonly string[] Boss = ["double_shift_day", "double_shift_night"];

    [Fact]
    public void HeadsShareHpAndStagger()
    {
        var b = NewBattle(["ines"], Boss);
        Exact(b);
        b.Run();                                       // the Night head (SPD 110) acts before Ines
        var (day, night) = (b.Enemies[0], b.Enemies[1]);
        Assert.Same(day.Body, night.Body);
        b.Minutes = 5;
        night.Body.Hp = 1400;
        (int hp, int stagger)? nightSees = null;       // read when the Burn lands, before any later turn
        b.Emitted += e => { if (e.Kind == EventKind.Status && e.Target == day) nightSees ??= (night.Body.Hp, night.Body.Stagger); };
        Act(b, "ines_flambe", day);                    // 2.2 × 32 × 100/130 × 1.25 = 67.7 → 68, Stagger 25
        Assert.Equal((1400 - 68, 140 - 25), nightSees);
        Assert.True(day.Has(StatusId.Burn));
        Assert.False(night.Has(StatusId.Burn));        // statuses stay on the head that was hit
        Assert.Contains("double_shift:Heat", b.Known);
    }

    [Fact]
    public void AoeAndRushHitTheBodyOnce()
    {
        var b = NewBattle(["noa", "ines"], Boss);
        var events = Record(b);
        b.Run();
        var body = b.Enemies[0].Body;
        while (b.Actor!.Def.Id != "ines") Act(b, "brace");
        b.Minutes = 5;
        int from = events.Count;
        Act(b, "ines_grease_fire");
        Assert.Single(events.Skip(from), e => e.Kind == EventKind.Damage && !e.Target!.IsParty);

        Settle(b);
        while (b.Actor!.Def.Id != "ines") Act(b, "brace");
        foreach (var e in b.Enemies.Where(e => e.Def.Body == null)) e.Body.Hp = 0;   // clear summoned Agenda Items
        body.Stagger = 10;
        Act(b, "ines_sear", b.Enemies[0]);             // breaks the body: Rush is offered
        Assert.True(b.RushReady);
        from = events.Count;
        Assert.True(b.Submit(new Command(CommandKind.Rush)));
        Assert.Equal(2, events.Skip(from).Count(e => e.Kind == EventKind.Damage && !e.Target!.IsParty));   // one per member
    }

    [Fact]
    public void StaggerSkipsBothHeads()
    {
        var b = NewBattle(["ines"], Boss);
        var events = Record(b);
        b.Run();
        var (day, night) = (b.Enemies[0], b.Enemies[1]);
        day.Body.Stagger = 10;
        Act(b, "ines_sear", day);
        int brk = events.FindIndex(e => e.Kind == EventKind.StaggerBreak);
        Assert.True(brk >= 0);
        foreach (var head in new[] { day, night })
            while (!events.Skip(brk).Any(e => e.Kind == EventKind.TurnStart && e.Target == head)) Act(b, "brace");
        foreach (var head in new[] { day, night })
        {
            int start = events.FindIndex(brk, e => e.Kind == EventKind.TurnStart && e.Target == head);
            var next = events.Skip(start + 1).First(e => e.Target == head && e.Kind is EventKind.Recover or EventKind.Action);
            Assert.Equal(EventKind.Recover, next.Kind);  // each head spends its next turn recovering
        }
        Assert.Equal(140, day.Body.Stagger);
        Assert.False(day.Body.Staggered);
    }

    [Fact]
    public void NoLightsOutBeforeTheClause()
    {
        var b = NewBattle(["ines"], Boss);
        b.Run();
        foreach (var head in b.Enemies)
            for (int i = 0; i < 50; i++)
                Assert.NotEqual("boss_lights_out", EnemyAi.RollIntent(b, head).Id);
    }

    [Fact]
    public void OvertimeClauseBelowHalf()
    {
        var b = NewBattle(["ines"], Boss);
        Exact(b);
        var events = Record(b);
        b.Run();
        var (day, night) = (b.Enemies[0], b.Enemies[1]);
        day.Body.Hp = 710;
        Assert.False(day.Body.ClauseActive);
        Act(b, "ines_sear", day);                      // 31 damage: 679 < 700
        Assert.True(day.Body.ClauseActive);
        Assert.Contains(events, e => e.Kind == EventKind.Log && e.Text.Contains("Overtime Clause"));
        Assert.Equal(10000 / 90.0 / 1.2, day.TurnLength, 6);
        Assert.Equal(10000 / 110.0 / 1.2, night.TurnLength, 6);
    }

    [Fact]
    public void LightsOutAfterCountdown()
    {
        var b = NewBattle(["noa", "bram"], Boss);
        Exact(b);
        var events = Record(b);
        int? counterAtClause = null, counterAfter = null;
        int clause = -1, lightsOut = -1;
        b.Emitted += e =>
        {
            if (e.Kind == EventKind.Log && e.Text.Contains("Overtime Clause") && clause < 0)
                (clause, counterAtClause) = (events.Count, b.Enemies[0].Body.Counter);
            if (e.Kind == EventKind.Action && e.Text.Contains("Lights Out") && lightsOut < 0) lightsOut = events.Count;
            if (e.Kind == EventKind.TurnStart && lightsOut >= 0 && counterAfter == null) counterAfter = b.Enemies[0].Body.Counter;
        };
        b.Run();
        var body = b.Enemies[0].Body;
        body.Hp = 710;
        while (b.Actor!.Def.Id != "noa") Act(b, "brace");
        Act(b, "noa_clock_in", b.Enemies[0]);          // 22 damage: the clause starts, "Shift ends in 4"
        Assert.Equal(4, counterAtClause);
        for (int i = 0; i < 30 && counterAfter == null; i++) Act(b, "brace");
        Assert.True(lightsOut > 0, "Lights Out never fired");

        int headTurns = events.Skip(clause).Take(lightsOut - clause).Count(e => e.Kind == EventKind.TurnStart && !e.Target!.IsParty && e.Target.Def.Body == "double_shift");
        Assert.InRange(headTurns, 5, 6);               // 4 turns count down, then the head holding Lights Out fires
        var hit = events.Skip(lightsOut).TakeWhile(e => e.Kind != EventKind.TurnStart).Where(e => e.Kind == EventKind.Damage).Select(e => e.Target);
        Assert.Contains(b.Party[0], hit);
        Assert.Contains(b.Party[1], hit);
        Assert.Equal(4, counterAfter);
    }

    [Fact]
    public void StaggerResetsShiftCountTo4()
    {
        var b = NewBattle(["ines"], Boss);
        b.Run();
        var (day, night) = (b.Enemies[0], b.Enemies[1]);
        day.Body.ClauseActive = true;
        day.Body.Counter = 2;
        day.Intent = b.Data.ActionById("boss_lights_out");
        day.Body.Stagger = 10;
        Act(b, "ines_sear", night);
        Assert.Equal(4, day.Body.Counter);
        Assert.Equal(Intent.Recover, day.Intent!.Intent);   // the charged Lights Out is cancelled
    }

    [Fact]
    public void UnpaidHoursStealsAndHeals5Percent()
    {
        var b = NewBattle(["ines"], Boss);
        var events = Record(b);
        b.Run();
        var night = b.Enemies[1];
        night.Intent = b.Data.ActionById("boss_unpaid_hours");
        night.Countdown = 0;                           // it goes right after Ines
        night.Body.Hp = 1000;
        b.Minutes = 3;
        Act(b, "brace");                               // +1 → 4, then Unpaid Hours takes 1 → 3
        Assert.Equal(1070, night.Body.Hp);             // +5% of 1,400
        Assert.Equal(3, b.Minutes);
    }

    [Fact]
    public void BodyDeathKillsBothHeadsAndWins()
    {
        var b = NewBattle(["ines"], Boss);
        var events = Record(b);
        b.Run();
        var (day, night) = (b.Enemies[0], b.Enemies[1]);
        day.Body.Hp = 1;
        Act(b, "ines_sear", day);
        Assert.False(day.Alive);
        Assert.False(night.Alive);
        Assert.Equal(2, events.Count(e => e.Kind == EventKind.KO));
        Assert.True(b.Won);
        Assert.Equal(BattlePhase.BattleEnd, b.Phase);
    }
}
