using AfterHour.Combat;

public class TimelineTests
{
    static readonly Tuning T = new();

    static Combatant Unit(int spd, bool party = true, double countdown = 0)
    {
        var u = Combatant.Create(new CombatantDef($"u{spd}", $"U{spd}", MaxHp: 100, Atk: 10, Def: 10, Spd: spd), party, T);
        u.Countdown = countdown;
        return u;
    }

    [Fact]
    public void SpeedSetsFrequency()
    {
        // Spec: over 1,000 time units, a SPD 120 unit acts 12 times and a SPD 100 unit acts 10.
        var fast = Unit(120);
        var slow = Unit(100);
        var units = new[] { fast, slow };
        foreach (var u in units) Timeline.Reset(u, Weight.Standard);   // battle start: countdown = turn length
        var count = new Dictionary<Combatant, int> { [fast] = 0, [slow] = 0 };
        double t = 0;
        while (true)
        {
            var n = Timeline.Next(units);
            var dt = n.Countdown;
            if (t + dt > 1000 + 1e-6) break;
            t += dt;
            Timeline.Pass(units, dt);
            count[n]++;
            Timeline.Reset(n, Weight.Standard);
        }
        Assert.Equal(12, count[fast]);
        Assert.Equal(10, count[slow]);
    }

    [Fact]
    public void DelayMath()
    {
        // Spec: a 25% Skip on a SPD 100 enemy with countdown 40 leaves it at 65.
        var u = Unit(100, party: false, countdown: 40);
        var added = Timeline.Delay(u, 0.25);
        Assert.Equal(65, u.Countdown, 9);
        Assert.Equal(25, added, 9);
    }

    [Fact]
    public void AdvanceFloorsAtZero()
    {
        var u = Unit(100, countdown: 10);
        var removed = Timeline.Advance(u, 0.3);
        Assert.Equal(0, u.Countdown);
        Assert.Equal(10, removed, 9);
    }

    [Fact]
    public void LowestCountdownActsFirst()
    {
        var enemy = Unit(100, party: false, countdown: 5);
        var ally = Unit(100, countdown: 10);
        Assert.Same(enemy, Timeline.Next(new[] { ally, enemy }));
    }

    [Fact]
    public void TiesGoToPartyThenSpeed()
    {
        var enemy = Unit(200, party: false);
        var slowAlly = Unit(100);
        var fastAlly = Unit(110);
        Assert.Same(fastAlly, Timeline.Next(new[] { enemy, slowAlly, fastAlly }));
    }

    [Fact]
    public void ResetUsesWeight()
    {
        var u = Unit(100, countdown: 7);
        Timeline.Reset(u, Weight.Light);
        Assert.Equal(75, u.Countdown, 9);
        Timeline.Reset(u, Weight.Heavy);
        Assert.Equal(130, u.Countdown, 9);
        Timeline.Reset(u, Weight.None);
        Assert.Equal(130, u.Countdown, 9);
    }

    [Fact]
    public void PreviewIsPureAndEightLong()
    {
        // fast: turn length 50, starts at 50. slow (enemy, no intent): turn length 100, starts at 100.
        // Hand-derived: fast@50, fast@100 (tie with slow, party first), slow@100, fast@150, fast@200, slow@200, fast@250, fast@300.
        var fast = Unit(200, countdown: 50);
        var slow = Unit(100, party: false, countdown: 100);
        var times = new List<double>();
        var order = Timeline.Preview(new[] { fast, slow }, 8, times: times);
        Assert.Equal(new[] { fast, fast, slow, fast, fast, slow, fast, fast }, order);
        Assert.Equal(new double[] { 50, 100, 100, 150, 200, 200, 250, 300 }, times);   // time until each turn
        Assert.Equal(50, fast.Countdown);
        Assert.Equal(100, slow.Countdown);
    }

    [Fact]
    public void PreviewUsesEnemyIntentWeightOnlyForItsFirstTurn()
    {
        // Enemy SPD 100 at 0 with a Heavy intent: acts at 0, then 130 (Heavy), then 230 (Standard).
        // Ally SPD 80 (turn length 125) at 110: acts at 110, 235.
        // Merged: e0, a110, e130, e230, a235. Ignoring the intent gives e,e,a,…; using it every turn gives e,a,e,a,….
        var enemy = Unit(100, party: false, countdown: 0);
        enemy.Intent = new ActionDef("slam", "Slam", Weight: Weight.Heavy);
        var ally = Unit(80, countdown: 110);
        var order = Timeline.Preview(new[] { enemy, ally }, 5);
        Assert.Equal(new[] { enemy, ally, enemy, enemy, ally }, order);
    }

    [Fact]
    public void SpeedFloorsAtOne()
    {
        var u = Unit(100);
        u.BaseSpd = 0;
        Assert.Equal(1, u.Spd);
        Assert.Equal(10000, u.TurnLength);
    }
}
