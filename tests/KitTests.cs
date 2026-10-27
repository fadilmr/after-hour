using AfterHour.Combat;
using static TestData;

public class KitTests
{
    [Fact]
    public void SkipMovesCountdown40To65()
    {
        // Spec: a 25% Skip on a SPD 100 enemy with countdown 40 leaves it at 65.
        var b = NewBattle(["noa"], ["agenda_item"]);   // Noa acts at 95.2, the Agenda Item (SPD 100) at 100
        double? atDelay = null;
        b.Emitted += e => { if (e.Kind == EventKind.Delay && e.Target == b.Enemies[0]) atDelay ??= e.Target.Countdown; };
        b.Run();
        b.Enemies[0].Countdown = 40;
        Act(b, "noa_skip", b.Enemies[0]);
        Assert.Equal(65, atDelay!.Value, 6);
    }

    [Fact]
    public void SkipCannotRepeatBeforeTargetActs()
    {
        var b = NewBattle(["noa"], ["deadline", "agenda_item"]);
        b.Run();
        var (dl, item) = (b.Enemies[0], b.Enemies[1]);
        var skip = b.Data.ActionById("noa_skip");
        dl.Countdown = 1000;                          // it won't act before Noa's next turn
        Act(b, "noa_skip", dl);
        Assert.DoesNotContain(dl, b.Targets(b.Actor!, skip));
        Assert.Contains(item, b.Targets(b.Actor!, skip));
        Assert.Contains(dl, b.Targets(b.Actor!, b.Data.ActionById("noa_clock_in")));   // only Skip is blocked
        dl.Countdown = 0;                             // now it acts before Noa again
        Act(b, "brace");
        Assert.Contains(dl, b.Targets(b.Actor!, skip));
    }

    [Fact]
    public void FastForwardedAllyActsNext()
    {
        var b = NewBattle(["noa", "ines"], ["deadline"]);
        var events = Record(b);
        b.Run();
        var (noa, ines) = (b.Party[0], b.Party[1]);
        Assert.Same(noa, b.Actor);
        ines.Countdown = 500;
        Assert.DoesNotContain(noa, b.Targets(noa, b.Data.ActionById("noa_fast_forward")));
        int from = events.Count;
        Act(b, "noa_fast_forward", ines);
        Assert.Same(ines, events.Skip(from).First(e => e.Kind == EventKind.TurnStart).Target);
    }

    [Fact]
    public void HypeUpGivesFocusAndAdvances30()
    {
        var b = NewBattle(["rook", "ines"], ["deadline"]);
        var events = Record(b);
        b.Run();
        var ines = b.Party[1];
        ines.Countdown = 200;
        Act(b, "rook_hype_up", ines);
        Assert.True(ines.Has(StatusId.Focus));
        Assert.Equal(30, Assert.Single(events, e => e.Kind == EventKind.Advance && e.Target == ines).Amount, 6);
    }

    [Fact]
    public void DoubleShiftActsAgainAndCosts()
    {
        var b = NewBattle(["ines"], ["deadline"]);
        b.Run();
        var (ines, dl) = (b.Party[0], b.Enemies[0]);
        var ds = b.Data.ActionById("ines_double_shift");
        var countdown = ines.Countdown;
        Act(b, "ines_double_shift");
        Assert.Equal(260 - 52, ines.Body.Hp);       // 20% of her max HP
        Assert.Equal(2, b.Minutes);
        Assert.Equal(BattlePhase.ChooseAction, b.Phase);
        Assert.Same(ines, b.Actor);
        Assert.Equal(countdown, ines.Countdown);
        // Usable once every 3 of her turns: refused for the rest of this turn and the next two.
        for (int turn = 0; turn < 3; turn++)
        {
            Assert.False(b.CanUse(ines, ds, out _));
            Act(b, "ines_sear", dl);
        }
        Assert.True(b.CanUse(ines, ds, out _));
    }

    [Fact]
    public void DoubleShiftRefusedWhenHpTooLow()
    {
        var b = NewBattle(["ines"], ["deadline"]);
        b.Run();
        var ines = b.Party[0];
        var ds = b.Data.ActionById("ines_double_shift");
        ines.Body.Hp = 52;                            // exactly the cost: she would drop to 0
        Assert.False(b.CanUse(ines, ds, out var why));
        Assert.Contains("HP", why);
        Assert.False(b.Submit(new Command(CommandKind.Act, ines, ds)));
        ines.Body.Hp = 53;
        Assert.True(b.CanUse(ines, ds, out _));
    }

    [Fact]
    public void BurnTicksAtTurnStartForTwoTurns()
    {
        var b = NewBattle(["ines"], ["deadline"]);
        Exact(b);
        var events = Record(b);
        b.Run();
        var dl = b.Enemies[0];
        dl.Body.Stagger = dl.Body.MaxStagger = 1000;  // no break, so its turns run normally
        Act(b, "ines_flambe", dl);
        while (events.Count(e => e.Kind == EventKind.TurnStart && e.Target == dl) < 3) Act(b, "brace");

        // A burn tick is the Damage event right after the Deadline's own TurnStart.
        var ticks = events.Select((e, i) => (e, i))
            .Where(x => x.e.Kind == EventKind.TurnStart && x.e.Target == dl)
            .Select(x => events[x.i + 1])
            .Select(next => next.Kind == EventKind.Damage && next.Target == dl ? next.Amount : 0)
            .ToList();
        Assert.Equal(new[] { 13.0, 13.0, 0.0 }, ticks);   // 8% of 160 = 12.8 → 13, twice, then gone
        Assert.False(dl.Has(StatusId.Burn));
    }

    [Fact]
    public void BurnKillAtTurnStartSkipsTurn()
    {
        var b = NewBattle(["ines"], ["deadline", "deadline"]);
        var events = Record(b);
        b.Run();
        var dl = b.Enemies[0];
        dl.Statuses.Add(new StatusInstance { Id = StatusId.Burn, TurnsLeft = 2 });
        dl.Body.Hp = 5;
        Act(b, "brace");
        int start = events.FindIndex(e => e.Kind == EventKind.TurnStart && e.Target == dl);
        Assert.True(start >= 0);
        Assert.False(dl.Alive);
        Assert.DoesNotContain(events.Skip(start), e => e.Kind == EventKind.Action && e.Target == dl);
        Assert.Equal(BattlePhase.ChooseAction, b.Phase);   // the other Deadline keeps the battle going
    }

    [Fact]
    public void ChillAndFatiguedSlowNextReset()
    {
        // Bram's Blue Period chills the Agenda Item (SPD 100): its next two turn gaps are 125, then 100 again.
        var b = NewBattle(["bram"], ["agenda_item"]);
        Exact(b);
        var events = Record(b);
        b.Run();
        var item = b.Enemies[0];
        Act(b, "bram_blue_period", item);              // 38 damage of 40 HP; Chill for 2 of its turns
        Assert.Equal(125, item.TurnLength, 6);
        while (events.Count(e => e.Kind == EventKind.TurnStart && e.Target == item) < 5) Act(b, "brace");
        var starts = events.Where(e => e.Kind == EventKind.TurnStart && e.Target == item).Select(e => e.Amount).ToList();
        Assert.Equal(125, starts[2] - starts[1], 6);
        Assert.Equal(125, starts[3] - starts[2], 6);
        Assert.Equal(100, starts[4] - starts[3], 6);

        item.Statuses.Add(new StatusInstance { Id = StatusId.Chill, TurnsLeft = 2 });
        item.Statuses.Add(new StatusInstance { Id = StatusId.Fatigued, TurnsLeft = 2 });
        Assert.Equal(156.25, item.TurnLength, 6);     // 100 / (0.8 × 0.8)
    }

    [Fact]
    public void JoltDelayCountsTowardCap()
    {
        // Spark (weak, then Jolt) + two Clock Ins: 10, then 10 + Jolt 5, then the cap leaves 5 and nothing for Jolt.
        var b = NewBattle(["noa"], ["agenda_item"]);
        var events = Record(b);
        b.Run();
        var item = b.Enemies[0];
        item.Countdown = 1000;
        item.Body.Hp = item.Body.MaxHp = 9999;
        item.Body.Stagger = item.Body.MaxStagger = 1000;
        Act(b, "noa_spark", item);
        Act(b, "noa_clock_in", item);
        Act(b, "noa_clock_in", item);
        var delays = events.Where(e => e.Kind == EventKind.Delay && e.Target == item).Select(e => Math.Round(e.Amount, 6));
        Assert.Equal(new[] { 10.0, 10.0, 5.0, 5.0 }, delays);   // 30% total, not 40
    }

    [Fact]
    public void EchoRepeatsNextHitAtHalf()
    {
        var b = NewBattle(["rook"], ["deadline"]);
        Exact(b);
        var events = Record(b);
        b.Run();
        var dl = b.Enemies[0];
        Act(b, "rook_drop_the_bass");                  // 16 damage, then Echo
        Assert.True(dl.Has(StatusId.Echo));
        int from = events.Count;
        Act(b, "rook_feedback", dl);                   // 0.9 × 24 × 100/120 = 18, echoed for 9
        var hits = events.Skip(from).Where(e => e.Kind == EventKind.Damage && e.Target == dl).Select(e => e.Amount);
        Assert.Equal(new[] { 18.0, 9.0 }, hits);
        Assert.False(dl.Has(StatusId.Echo));
    }

    [Fact]
    public void LineCookBoostsHeatOnBurning()
    {
        var b = NewBattle(["ines"], ["deadline"]);
        Exact(b);
        var events = Record(b);
        b.Run();
        var dl = b.Enemies[0];
        Act(b, "ines_flambe", dl);                     // Burn on
        int from = events.Count;
        Act(b, "ines_sear", dl);                       // 32 × 100/120 × 1.25 = 33.3, ×1.2 = 40
        Assert.Equal(40, events.Skip(from).First(e => e.Kind == EventKind.Damage && e.Target == dl).Amount);
    }

    [Fact]
    public void NightWatchTakesOneHitPerEnemyAction()
    {
        // AoE: Final Notice on Noa (<30%), Rook (<30%) and Bram. Only the first low ally's hit is taken over.
        var b = NewBattle(["noa", "rook", "bram"], ["deadline"]);
        Exact(b);
        var events = Record(b);
        b.Run();                                       // Rook acts first
        var (noa, rook, bram) = (b.Party[0], b.Party[1], b.Party[2]);
        noa.Body.Hp = 50;
        rook.Body.Hp = 50;
        b.Enemies[0].Intent = b.Data.ActionById("deadline_final_notice");
        b.Enemies[0].Countdown = 0;
        int from = events.Count;
        Act(b, "brace");                               // Rook guards; the Deadline goes next
        var hit = events.Skip(from).Where(e => e.Kind == EventKind.Damage).Select(e => e.Target).ToList();
        Assert.Equal(2, hit.Count(t => t == bram));
        Assert.Equal(1, hit.Count(t => t == rook));
        Assert.Equal(0, hit.Count(t => t == noa));

        // Multi-hit: Rush Hour's three hits on Noa (<30%) → first to Bram, the other two to Noa.
        for (int seed = 1; seed <= 30; seed++)
        {
            var r = NewBattle(["noa", "bram"], ["rush_hour"], seed: seed);
            var ev = Record(r);
            r.Party[0].Body.Hp = 60;
            r.Run();                                   // Rush Hour (SPD 120) acts first
            var targets = ev.Where(e => e.Kind == EventKind.Damage).Take(3).Select(e => e.Target).ToList();
            if (targets.All(t => t == r.Party[1])) continue;   // Jostle picked Bram himself; try another seed
            Assert.Equal(new[] { r.Party[1], r.Party[0], r.Party[0] }, targets);
            return;
        }
        Assert.Fail("No seed made Rush Hour target Noa.");
    }

    [Fact]
    public void PaintOverShieldAbsorbsBeforeHp()
    {
        var b = NewBattle(["bram", "noa"], ["deadline"]);
        Exact(b);
        var events = Record(b);
        b.Run();                                       // Noa (95.2) first, then Bram (111.1)
        var (bram, noa) = (b.Party[0], b.Party[1]);
        var dl = b.Enemies[0];
        Act(b, "noa_clock_in", dl);                    // pushes the Deadline back to 137.5, after Bram
        Settle(b);                                     // decline the Shift Change prompt
        Assert.Same(bram, b.Actor);
        dl.Intent = b.Data.ActionById("deadline_final_notice");
        Act(b, "bram_paint_over", noa);                // shield = 20% of Bram's 360 = 72; then Final Notice lands
        Assert.Equal(72, Assert.Single(events, e => e.Kind == EventKind.Shield && e.Target == noa).Amount);
        Assert.Equal(300, noa.Body.Hp);                // 2.5 × 28 × 100/120 = 58, all blocked
        Assert.Equal(14, noa.Shield);

        while (b.Actor != bram) Act(b, "brace");
        noa.Shield = 100;
        Act(b, "bram_paint_over", noa);                // a smaller shield doesn't replace a bigger one
        Assert.Equal(100, noa.Shield);
    }

    [Fact]
    public void ItemsUseInventoryNotMinutes()
    {
        var b = NewBattle(["noa"], ["deadline"]);
        b.Run();
        var noa = b.Party[0];
        noa.Body.Hp = 100;
        Act(b, "item_noodles", noa);                   // heal 40% of 300
        Assert.Equal(220, noa.Body.Hp);
        Assert.Equal(2, b.Items["item_noodles"]);
        Assert.Equal(3, b.Minutes);

        b.Items["item_noodles"] = 0;
        Assert.False(b.CanUse(noa, b.Data.ActionById("item_noodles"), out _));
        Assert.False(b.Submit(new Command(CommandKind.Act, noa, b.Data.ActionById("item_noodles"), noa)));

        noa.Statuses.Add(new StatusInstance { Id = StatusId.Pressure, TurnsLeft = 2 });
        noa.Statuses.Add(new StatusInstance { Id = StatusId.Focus, TurnsLeft = 2 });
        Act(b, "item_painkillers", noa);
        Assert.Empty(noa.Statuses);

        Act(b, "item_coffee");
        Assert.Equal(5, b.Minutes);
    }

    [Fact]
    public void KoMembersAreSkippedAndUntargetable()
    {
        var b = NewBattle(["noa", "rook", "ines"], ["deadline"]);
        var events = Record(b);
        var ines = b.Party[2];
        ines.Body.Hp = 0;
        b.Run();                                       // Rook first
        var rook = b.Actor!;
        foreach (var id in new[] { "rook_hype_up", "item_noodles" })
            Assert.DoesNotContain(ines, b.Targets(rook, b.Data.ActionById(id)));
        Assert.False(b.Submit(new Command(CommandKind.Act, rook, b.Data.ActionById("rook_hype_up"), ines)));
        for (int i = 0; i < 4; i++) Act(b, "brace");
        Assert.DoesNotContain(events, e => e.Kind == EventKind.TurnStart && e.Target == ines);
    }
}
