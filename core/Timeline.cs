using System;
using System.Collections.Generic;
using System.Linq;

namespace AfterHour.Combat;

/// <summary>The speed Timeline: every unit counts down, and the lowest countdown acts next.</summary>
public static class Timeline
{
    /// <summary>Lowest countdown acts. Ties go to the party first, then to the higher SPD.</summary>
    public static Combatant Next(IEnumerable<Combatant> living) => Next(living, u => u.Countdown);

    // Rounding makes float noise (499.99999 vs 500) count as a tie.
    static Combatant Next(IEnumerable<Combatant> living, Func<Combatant, double> countdown) =>
        living.OrderBy(u => Math.Round(countdown(u), 6))
              .ThenByDescending(u => u.IsParty)
              .ThenByDescending(u => u.Spd)
              .First();

    /// <summary>Time passes for everyone.</summary>
    public static void Pass(IEnumerable<Combatant> living, double elapsed)
    {
        foreach (var u in living) u.Countdown = Math.Max(0, u.Countdown - elapsed);
    }

    /// <summary>After acting: turn length × the action's weight. Weight.None leaves the countdown alone.</summary>
    public static void Reset(Combatant u, Weight w)
    {
        if (w != Weight.None) u.Countdown = u.TurnLength * u.Tuning.WeightFactor(w);
    }

    /// <summary>Pushes the unit back by a fraction of its turn length. Returns the time added.</summary>
    public static double Delay(Combatant u, double fraction)
    {
        var added = u.TurnLength * fraction;
        u.Countdown += added;
        return added;
    }

    /// <summary>Pulls the unit forward by a fraction of its turn length, never below 0. Returns the time removed.</summary>
    public static double Advance(Combatant u, double fraction)
    {
        var removed = Math.Min(u.Countdown, u.TurnLength * fraction);
        u.Countdown -= removed;
        return removed;
    }

    /// <summary>
    /// The next n turns, without changing anyone. Party turns assume a Standard action; an enemy's
    /// first turn uses its queued intent's weight. Pass countdowns to preview a hypothetical state,
    /// and a times list to receive how long from now each turn comes.
    /// </summary>
    public static List<Combatant> Preview(IEnumerable<Combatant> living, int n, IDictionary<Combatant, double>? countdowns = null, List<double>? times = null)
    {
        var units = living.ToList();
        var order = new List<Combatant>(n);
        if (units.Count == 0) return order;

        var cd = units.ToDictionary(u => u, u => countdowns != null && countdowns.TryGetValue(u, out var c) ? c : u.Countdown);
        var seen = new HashSet<Combatant>();
        double now = 0;
        for (int i = 0; i < n; i++)
        {
            var next = Next(units, u => cd[u]);
            var elapsed = cd[next];
            foreach (var u in units) cd[u] = Math.Max(0, cd[u] - elapsed);
            now += elapsed;
            times?.Add(now);
            order.Add(next);

            var w = !next.IsParty && seen.Add(next) && next.Intent is { } intent ? intent.Weight : Weight.Standard;
            cd[next] = next.TurnLength * next.Tuning.WeightFactor(w);
        }
        return order;
    }
}
