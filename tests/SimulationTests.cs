using AfterHour.Combat;
using Xunit.Abstractions;
using static TestData;

public class SimulationTests(ITestOutputHelper output)
{
    [Fact]
    public void HonestPreview()
    {
        // Spec: with no cut-ins, the board's 8-turn preview matches the order that actually plays out.
        var data = Load();
        data.Combatants.Add(new CombatantDef("dummy_a", "Dummy A", 9999, Atk: 10, Def: 20, Spd: 95, MaxStagger: 100, Actions: ["enemy_attack"]));
        data.Combatants.Add(new CombatantDef("dummy_b", "Dummy B", 9999, Atk: 10, Def: 20, Spd: 108, MaxStagger: 100, Actions: ["enemy_attack"]));
        var b = NewBattle(["noa", "rook", "ines"], ["dummy_a", "dummy_b"], seed: 3, data: data);
        var actors = new List<Combatant>();
        b.Emitted += e => { if (e.Kind == EventKind.TurnStart) actors.Add(e.Target!); };
        b.Run();
        var preview = b.Preview(8).ToList();
        while (actors.Count < 8) Act(b, b.Actor!.Def.Actions![0], b.Enemies[0]);
        Assert.Equal(preview, actors.Take(8));
    }

    [Fact]
    public void PreviewWithSkipShowsTargetLater()
    {
        var b = NewBattle(["noa"], ["agenda_item"]);
        b.Run();                                       // Noa's turn
        var (noa, item) = (b.Party[0], b.Enemies[0]);
        item.Countdown = 60;
        var snapshot = b.Living.ToDictionary(u => u, u => u.Countdown);
        var withBrace = b.Preview(8, new Command(CommandKind.Act, noa, b.Data.ActionById("brace")));
        var withSkip = b.Preview(8, new Command(CommandKind.Act, noa, b.Data.ActionById("noa_skip"), item));
        Assert.Same(item, withBrace[0]);               // after a Light Brace (71.4), the item at 60 still goes first
        Assert.Same(noa, withSkip[0]);                 // Skip pushes it to 85, behind Noa's next turn
        Assert.Same(item, withSkip[1]);
        Assert.All(snapshot, kv => Assert.Equal(kv.Value, kv.Key.Countdown));
        Assert.Equal(BattlePhase.ChooseAction, b.Phase);
        Assert.Equal(3, b.Minutes);
    }

    public static TheoryData<string, StartMode> Encounters()
    {
        var cases = new TheoryData<string, StartMode>();
        foreach (var e in Load().Encounters)
            foreach (var mode in Enum.GetValues<StartMode>())
                cases.Add(e.Id, mode);
        return cases;
    }

    [Theory]
    [MemberData(nameof(Encounters))]
    public void EveryEncounterFinishes(string encounter, StartMode mode)
    {
        var data = Load();
        var enemies = data.Encounters.Single(e => e.Id == encounter).Enemies.ToArray();
        var b = NewBattle(["noa", "rook", "ines"], enemies, mode, seed: 7, data: data);
        int maxHp = b.Party.Sum(p => p.Body.MaxHp);
        b.Run();
        while (b.Phase != BattlePhase.BattleEnd && b.TurnCount <= 500)
            Assert.True(b.Submit(AutoPilot.Choose(b)), $"AutoPilot chose an illegal move in {b.Phase}");
        Assert.Equal(BattlePhase.BattleEnd, b.Phase);
        double lost = 100.0 * (maxHp - b.Party.Sum(p => p.Body.Hp)) / maxHp;
        output.WriteLine($"{encounter,-15} {mode,-10} {(b.Won ? "won" : "lost"),-4} turns={b.TurnCount,3} partyHpLost={lost:0}%");
    }
}
