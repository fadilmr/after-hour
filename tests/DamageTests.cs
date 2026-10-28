using AfterHour.Combat;

public class DamageTests
{
    static readonly string[] PartyIds = ["noa", "rook", "ines", "bram"];

    [Fact]
    public void DataLoadsAndAllIdsResolve()
    {
        var d = TestData.Load();
        foreach (var c in d.Combatants)
        {
            foreach (var a in c.Actions ?? []) Assert.Equal(a, d.ActionById(a).Id);
            if (c.Overtime is { } ot) Assert.Equal(ot, d.ActionById(ot).Id);
        }
        foreach (var e in d.Encounters)
            foreach (var id in e.Enemies) Assert.Equal(id, d.UnitById(id).Id);
        foreach (var i in d.Items) Assert.Equal(i.Action, d.ActionById(i.Action).Id);
        Assert.Equal("brace", d.ActionById(d.Brace).Id);

        Assert.All(PartyIds, id => Assert.NotNull(d.UnitById(id).Overtime));
        Assert.Equal(2, d.Combatants.Count(c => c.Body == "double_shift"));
        Assert.Equal(6, d.Combatants.Count(c => c.Body == null && !PartyIds.Contains(c.Id)));
        Assert.Equal(6, d.Encounters.Count);
    }

    [Fact]
    public void UnknownIdThrowsWithItsName()
    {
        var d = TestData.Load();
        var ex = Assert.Throws<KeyNotFoundException>(() => d.ActionById("no_such_move"));
        Assert.Contains("no_such_move", ex.Message);
        Assert.Throws<KeyNotFoundException>(() => d.UnitById("no_such_unit"));
    }

    [Fact]
    public void JsonRoundTrips()
    {
        var d = TestData.Load();
        d.Tuning.WeakMult = 1.4;   // a tuned value must survive the trip
        var again = CombatData.Load(d.ToJson());
        Assert.Equal(d.Actions.Count, again.Actions.Count);
        Assert.Equal(d.Combatants.Count, again.Combatants.Count);
        Assert.Equal(d.Encounters.Count, again.Encounters.Count);
        Assert.Equal(d.Items.Count, again.Items.Count);
        Assert.Equal(32, again.UnitById("ines").Atk);
        Assert.Equal(1.4, again.Tuning.WeakMult);
        var burn = again.ActionById("ines_flambe").Effects![0];
        Assert.Equal((EffectType.Status, StatusId.Burn, 2), (burn.Type, burn.Status, burn.Turns));
        Assert.Equal(Weight.None, again.ActionById("ines_double_shift").Weight);
    }

    [Fact]
    public void FlambeOnDeadlineIsAbout73()
    {
        // Spec: 2.2 × 32 × 100/120 × 1.25 ≈ 73.
        var d = TestData.Load();
        d.Tuning.CritChance = 0;
        d.Tuning.SpreadMin = d.Tuning.SpreadMax = 1;
        var ines = Combatant.Create(d.UnitById("ines"), true, d.Tuning);
        var deadline = Combatant.Create(d.UnitById("deadline"), false, d.Tuning);
        var hit = Damage.Compute(d.Tuning, ines, deadline, d.ActionById("ines_flambe"), 0, new Random(1));
        Assert.Equal(73, hit.Amount);
        Assert.True(hit.Weak);
        Assert.False(hit.Resist);
        Assert.False(hit.Crit);
    }

    // Base case: ATK 100, Power 1.0, DEF 0 → 100 damage. Each row changes one thing.
    [Theory]
    [InlineData("plain", 100)]
    [InlineData("weak", 125)]
    [InlineData("resist", 50)]
    [InlineData("staggered", 150)]
    [InlineData("focus", 125)]
    [InlineData("pressure", 120)]
    [InlineData("guard", 60)]
    [InlineData("chain2", 140)]
    [InlineData("chain5", 160)]
    [InlineData("linecook", 120)]
    [InlineData("linecook_unburned", 100)]
    [InlineData("crit", 150)]
    [InlineData("rush", 120)]
    public void ModifiersMultiply(string mod, int expected)
    {
        var t = new Tuning { CritChance = 0, SpreadMin = 1, SpreadMax = 1 };
        var lineCook = mod.StartsWith("linecook");
        var attacker = Combatant.Create(new CombatantDef("a", "A", 100, Atk: 100, Def: 0, Spd: 100,
            Passives: lineCook ? ["line_cook"] : null), true, t);
        var target = Combatant.Create(new CombatantDef("b", "B", 999, Atk: 10, Def: 0, Spd: 100, MaxStagger: 50,
            Weak: mod is "weak" or "rush" ? [Element.Physical] : null,
            Resist: mod == "resist" ? [Element.Physical] : null), false, t);
        var action = new ActionDef("x", "X", lineCook ? Element.Heat : Element.Physical, Power: 1.0);
        int chain = 0;
        switch (mod)
        {
            case "staggered": target.Body.Staggered = true; break;
            case "focus": attacker.Statuses.Add(new StatusInstance { Id = StatusId.Focus, TurnsLeft = 2 }); break;
            case "pressure": target.Statuses.Add(new StatusInstance { Id = StatusId.Pressure, TurnsLeft = 2 }); break;
            case "guard": target.Statuses.Add(new StatusInstance { Id = StatusId.Guard }); break;
            case "chain2": chain = 2; break;
            case "chain5": chain = 5; break;
            case "linecook": target.Statuses.Add(new StatusInstance { Id = StatusId.Burn, TurnsLeft = 2 }); break;
            case "crit": t.CritChance = 1; break;
        }
        var hit = Damage.Compute(t, attacker, target, action, chain, new Random(1), rush: mod == "rush");
        Assert.Equal(expected, hit.Amount);
        Assert.Equal(mod == "crit", hit.Crit);
        Assert.Equal(mod == "weak", hit.Weak);   // Rush never counts as a weakness hit
    }

    [Fact]
    public void KitchenFireIgnoresHalfDef()
    {
        // 4.0 × 32 × 100/(100 + 30×0.5) × 1.25 (weak to Heat) = 139.13 → 139. Full DEF would give 123.
        var d = TestData.Load();
        d.Tuning.CritChance = 0;
        d.Tuning.SpreadMin = d.Tuning.SpreadMax = 1;
        var ines = Combatant.Create(d.UnitById("ines"), true, d.Tuning);
        var head = Combatant.Create(d.UnitById("double_shift_day"), false, d.Tuning);
        Assert.Equal(139, Damage.Compute(d.Tuning, ines, head, d.ActionById("ines_kitchen_fire"), 0, new Random(1)).Amount);
    }

    [Fact]
    public void MinimumDamageIsOne()
    {
        var t = new Tuning { CritChance = 0, SpreadMin = 1, SpreadMax = 1 };
        var attacker = Combatant.Create(new CombatantDef("a", "A", 100, Atk: 1, Def: 0, Spd: 100), true, t);
        var wall = Combatant.Create(new CombatantDef("w", "Wall", 100, Atk: 1, Def: 999, Spd: 100), false, t);
        Assert.Equal(1, Damage.Compute(t, attacker, wall, new ActionDef("x", "X", Element.Physical, Power: 0.1), 0, new Random(1)).Amount);
    }

    [Fact]
    public void SpreadStaysWithinBounds()
    {
        // Spread ×0.95–×1.05 around 100: every roll lands in 95..105, and not all rolls are equal.
        var t = new Tuning { CritChance = 0 };
        var a = Combatant.Create(new CombatantDef("a", "A", 100, Atk: 100, Def: 0, Spd: 100), true, t);
        var b = Combatant.Create(new CombatantDef("b", "B", 999, Atk: 1, Def: 0, Spd: 100), false, t);
        var rng = new Random(7);
        var rolls = Enumerable.Range(0, 200).Select(_ => Damage.Compute(t, a, b, new ActionDef("x", "X", Element.Physical, Power: 1.0), 0, rng).Amount).ToList();
        Assert.All(rolls, r => Assert.InRange(r, 95, 105));
        Assert.True(rolls.Distinct().Count() > 3);
    }
}

public class ForecastTests
{
    [Fact]
    public void ExpectedSitsInsideTheRolledRange()
    {
        var b = TestData.NewBattle(["ines"], ["deadline"]);
        b.Data.Tuning.CritChance = 0;
        var ines = b.Party[0];
        var dl = b.Enemies[0];
        var flambe = b.Data.ActionById("ines_flambe");
        double expected = Damage.Expected(b.Data.Tuning, ines, dl, flambe, 0);
        var rng = new Random(3);
        var rolls = Enumerable.Range(0, 200).Select(_ => Damage.Compute(b.Data.Tuning, ines, dl, flambe, 0, rng).Amount).ToList();
        Assert.InRange(expected, rolls.Min() - 0.5, rolls.Max() + 0.5);
        Assert.InRange(expected, 0.95 * rolls.Average(), 1.05 * rolls.Average());
    }
}
