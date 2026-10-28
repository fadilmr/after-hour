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
    public void DeadlineChargesTwoTurnsThenFinalNotice()
    {
        var b = NewBattle(["noa", "rook"], ["deadline"]);
        Exact(b);
        var events = Record(b);
        var dl = b.Enemies[0];
        var intents = IntentsAtTurnStart(b, dl);
        b.Run();
        while (Actions(events, dl) < 3) Act(b, "brace");
        Assert.Equal(new[] { Intent.Charge, Intent.Charge, Intent.Heavy }, intents.Select(a => a.Intent));
        Assert.Equal("deadline_final_notice", intents[2].Id);

        // Final Notice hits every member at Power 2.5 (both braced, ×0.6): Noa 139.6 → 84, Rook 141.9 → 85.
        int notice = events.FindLastIndex(e => e.Kind == EventKind.Action && e.Target == dl);
        var hits = events.Skip(notice).Where(e => e.Kind == EventKind.Damage).ToDictionary(e => e.Target!, e => e.Amount);
        Assert.Equal(84, hits[b.Party[0]]);
        Assert.Equal(85, hits[b.Party[1]]);
        Assert.Equal(2, dl.Body.Counter);
    }

    [Fact]
    public void StaggeringDeadlineAtCount1ResetsTo2()
    {
        // Spec: staggering a Deadline at count 1 resets it to 2.
        var b = NewBattle(["noa"], ["deadline"]);
        var events = Record(b);
        var dl = b.Enemies[0];
        b.Run();
        Assert.Equal(2, dl.Body.Counter);
        while (Actions(events, dl) < 1) Act(b, "brace");
        Assert.Equal(1, dl.Body.Counter);
        dl.Body.Stagger = 20;
        Act(b, "noa_spark", dl);                       // weak to Volt, Stagger 20: breaks it
        Assert.Equal(2, dl.Body.Counter);
    }

    [Fact]
    public void EndlessMeetingSummonsEveryThirdTurnUpToTwo()
    {
        var b = NewBattle(["noa"], ["endless_meeting"]);
        var events = Record(b);
        var meeting = b.Enemies[0];
        var intents = IntentsAtTurnStart(b, meeting);
        b.Party[0].Body.Hp = b.Party[0].Body.MaxHp = 10000;   // survive nine turns of bracing
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
        Assert.Equal(3, firstHits.Count);              // 0.4 × 58 × 100/128 = 18.1 → 18, three times, on Bram
        Assert.All(firstHits, h => Assert.Equal((b.Party[0], 18.0), h));

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
        dl.Body.Stagger = 20;
        Act(b, "noa_spark", dl);                       // weak to Volt, Stagger 20: breaks it
        Assert.Equal(Intent.Recover, atBreak);
    }
}

public class IntentTargetTests
{
    [Fact]
    public void EnemyHitsTheTargetItShowed()
    {
        for (int seed = 1; seed <= 20; seed++)
        {
            var b = TestData.NewBattle(["noa", "rook", "ines"], ["rush_hour"], seed: seed);
            b.PauseAfterEachTurn = true;
            b.Run();                                   // start: intents roll
            var rh = b.Enemies[0];
            var shown = rh.IntentTarget;
            Assert.NotNull(shown);
            Assert.True(shown!.IsParty);
            var events = TestData.Record(b);
            b.Run();                                   // Rush Hour (SPD 120) acts first
            Assert.All(events.Where(e => e.Kind == EventKind.Damage), e => Assert.Equal(shown, e.Target));
        }
    }

    [Fact]
    public void SelfIntentHasNoTarget()
    {
        var b = TestData.NewBattle(["noa"], ["deadline"]);
        b.PauseAfterEachTurn = true;
        b.Run();
        Assert.Equal(Intent.Charge, b.Enemies[0].Intent!.Intent);
        Assert.Null(b.Enemies[0].IntentTarget);
    }
}
