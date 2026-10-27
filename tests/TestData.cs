using AfterHour.Combat;

static class TestData
{
    static readonly string Json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "combat.json"));

    /// <summary>A fresh copy of the real data each call, so a test can edit stats and tuning freely.</summary>
    public static CombatData Load() => CombatData.Load(Json);

    /// <summary>Full-HP party at bond rank 2 (Noa has no rank), the real item stock, nothing known yet.</summary>
    public static Battle NewBattle(string[] party, string[] enemies, StartMode mode = StartMode.Normal, int seed = 1, CombatData? data = null)
    {
        data ??= Load();
        var setup = party.Select(id => new MemberSetup(id, data.UnitById(id).MaxHp, 0, id == "noa" ? null : 2)).ToList();
        var items = data.Items.ToDictionary(i => i.Action, i => i.Start);
        return new Battle(data, setup, new EncounterDef("test", "Test", enemies), mode, items, new HashSet<string>(), seed);
    }

    /// <summary>No crits, no spread: damage is exactly the formula.</summary>
    public static void Exact(Battle b)
    {
        b.Data.Tuning.CritChance = 0;
        b.Data.Tuning.SpreadMin = b.Data.Tuning.SpreadMax = 1;
    }

    /// <summary>Declines whatever prompt is open (cut-in window, Reactions) until a turn needs a choice.</summary>
    public static void Settle(Battle b)
    {
        while (b.Phase is BattlePhase.TurnStart or BattlePhase.Reactions)
            if (b.AwaitingInput) Assert.True(b.Submit(new Command(CommandKind.Pass)));
            else b.Run();                              // paused between turns
    }

    /// <summary>Declines any open prompt, then submits an action for the current actor and asserts it was legal.</summary>
    public static void Act(Battle b, string actionId, Combatant? target = null)
    {
        Settle(b);
        Assert.Equal(BattlePhase.ChooseAction, b.Phase);
        Assert.True(b.Submit(new Command(CommandKind.Act, b.Actor, b.Data.ActionById(actionId), target)), $"{actionId} was refused");
    }

    public static List<BattleEvent> Record(Battle b)
    {
        var events = new List<BattleEvent>();
        b.Emitted += events.Add;
        return events;
    }
}
