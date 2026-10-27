using AfterHour.Combat;
using static TestData;

public class EnemyTests
{
    /// <summary>The intent each listed enemy executes, captured when its turn starts.</summary>
    static List<ActionDef> IntentsAtTurnStart(Battle b, Combatant enemy)
    {
        var list = new List<ActionDef>();
        b.Emitted += e => { if (e.Kind == EventKind.TurnStart && e.Target == enemy) list.Add(enemy.Intent!); };
        return list;
    }

    static int Actions(List<BattleEvent> events, Combatant u) => events.Count(e => e.Kind == EventKind.Action && e.Target == u);

    [Fact]
    public void DeadlineChargesThreeTurnsThenFinalNotice()
    {
        var b = NewBattle(["noa", "rook"], ["deadline"]);
        Exact(b);
        var events = Record(b);
        var dl = b.Enemies[0];
        var intents = IntentsAtTurnStart(b, dl);
        b.Run();
        while (Actions(events, dl) < 4) Act(b, "brace");
        Assert.Equal(new[] { Intent.Charge, Intent.Charge, Intent.Charge, Intent.Heavy }, intents.Select(a => a.Intent));
        Assert.Equal("deadline_final_notice", intents[3].Id);

        // Final Notice hits every member at Power 2.5 (both braced, ×0.6): Noa 58.3 → 35, Rook 59.3 → 36.
        int notice = events.FindLastIndex(e => e.Kind == EventKind.Action && e.Target == dl);
        var hits = events.Skip(notice).Where(e => e.Kind == EventKind.Damage).ToDictionary(e => e.Target!, e => e.Amount);
        Assert.Equal(35, hits[b.Party[0]]);
        Assert.Equal(36, hits[b.Party[1]]);
        Assert.Equal(3, dl.Body.Counter);
    }

    [Fact]
    public void StaggeringDeadlineAtCount1ResetsTo3()
    {
        // Spec: staggering a Deadline at count 1 resets it to 3.
        var b = NewBattle(["noa"], ["deadline"]);
        var events = Record(b);
        var dl = b.Enemies[0];
        b.Run();
        Assert.Equal(3, dl.Body.Counter);
        while (Actions(events, dl) < 2) Act(b, "brace");
        Assert.Equal(1, dl.Body.Counter);
        dl.Body.Stagger = 10;
        Act(b, "noa_clock_in", dl);                    // weak to Physical, Stagger 10: breaks it
        Assert.Equal(3, dl.Body.Counter);
    }

    [Fact]
    public void EndlessMeetingSummonsEveryThirdTurnUpToTwo()
    {
        var b = NewBattle(["noa"], ["endless_meeting"]);
        var events = Record(b);
        var meeting = b.Enemies[0];
        var intents = IntentsAtTurnStart(b, meeting);
        b.Run();
        while (intents.Count < 9) Act(b, "brace");
        var expected = new[] { "enemy_attack", "enemy_attack", "endless_meeting_summon",
                               "enemy_attack", "enemy_attack", "endless_meeting_summon",
                               "enemy_attack", "enemy_attack", "enemy_attack" };   // turn 9: two Agenda Items already
        Assert.Equal(expected, intents.Select(a => a.Id));
        Assert.Equal(2, events.Count(e => e.Kind == EventKind.Summon));
        Assert.Equal(2, b.Enemies.Count(e => e.Alive && e.Def.Id == "agenda_item"));
    }

    [Fact]
    public void RushHourCycleWeights()
    {
        var b = NewBattle(["bram"], ["rush_hour"]);
        Exact(b);
        var events = Record(b);
        var rh = b.Enemies[0];
        var intents = IntentsAtTurnStart(b, rh);
        b.Run();                                       // Rush Hour (SPD 120) acts before Bram
        var firstHits = events.Where(e => e.Kind == EventKind.Damage).Select(e => (e.Target, e.Amount)).ToList();
        Assert.Equal(3, firstHits.Count);              // 0.4 × 24 × 100/128 = 7.5 → 8, three times, on Bram
        Assert.All(firstHits, h => Assert.Equal((b.Party[0], 8.0), h));

        while (intents.Count < 7) Act(b, "brace");
        var expected = new[] { Weight.Standard, Weight.Standard, Weight.Light, Weight.Light, Weight.Light, Weight.Heavy, Weight.Standard };
        Assert.Equal(expected, intents.Select(a => a.Weight));
    }

    [Fact]
    public void HoldMusicPleaseHoldDelaysMember25()
    {
        var b = NewBattle(["noa"], ["hold_music"]);   // Noa 95.2, Hold Music 111.1
        var events = Record(b);
        b.Run();
        var noa = b.Party[0];
        Act(b, "brace");
        var delay = Assert.Single(events, e => e.Kind == EventKind.Delay && e.Target == noa);
        Assert.Equal(0.25 * noa.TurnLength, delay.Amount, 6);
        Assert.Equal("enemy_attack", b.Enemies[0].Intent!.Id);   // it alternates
    }

    [Fact]
    public void InsomniaNeverStealsFromEmptyPool()
    {
        var b = NewBattle(["noa"], ["insomnia"]);
        b.Run();
        var ins = b.Enemies[0];
        b.Minutes = 0;
        var rolls = Enumerable.Range(0, 50).Select(_ => EnemyAi.RollIntent(b, ins).Id).ToList();
        Assert.All(rolls, id => Assert.Equal("insomnia_fatigue", id));
        b.Minutes = 3;
        rolls = Enumerable.Range(0, 50).Select(_ => EnemyAi.RollIntent(b, ins).Id).ToList();
        Assert.Contains("insomnia_steal", rolls);
        Assert.Contains("insomnia_fatigue", rolls);
    }

    [Fact]
    public void DelayKeepsIntentButStaggerCancelsIt()
    {
        var b = NewBattle(["noa"], ["deadline"]);
        var dl = b.Enemies[0];
        ActionDef? atDelay = null;
        Intent? atBreak = null;
        b.Emitted += e =>
        {
            if (e.Kind == EventKind.Delay && e.Target == dl) atDelay ??= dl.Intent;
            if (e.Kind == EventKind.StaggerBreak) atBreak = dl.Intent?.Intent;
        };
        b.Run();
        var before = dl.Intent;
        Act(b, "noa_skip", dl);
        Assert.Same(before, atDelay);
        dl.Body.Stagger = 10;
        Act(b, "noa_clock_in", dl);
        Assert.Equal(Intent.Recover, atBreak);
    }
}
