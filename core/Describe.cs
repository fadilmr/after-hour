using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AfterHour.Combat;

/// <summary>
/// Player-facing text for actions, statuses and passives, generated from the same data the rules use, so the
/// descriptions can't drift from the numbers while you tune. Always "2.2" and "20%", whatever the system language.
/// </summary>
public static class Describe
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <param name="enemy">Describe from an enemy's point of view ("one party member", "its next turn").</param>
    public static string Action(ActionDef a, CombatData data, bool enemy = false)
    {
        var parts = new List<string>();
        if (a.Power > 0)
        {
            var element = a.Element == Element.None ? "" : $"{a.Element}, ";
            var hits = a.Hits > 1 ? $" × {a.Hits} hits" : "";
            var stagger = a.Stagger > 0 ? $", Stagger {a.Stagger}" : "";
            parts.Add($"{element}Power {a.Power.ToString("0.0#", Inv)}{hits}{stagger} to {Who(a.Target, enemy)}.");
        }
        if (a.DefIgnore > 0) parts.Add($"Ignores {Pct(a.DefIgnore)} of DEF.");
        foreach (var fx in a.Effects ?? []) parts.Add(Effect(fx, a, data, enemy));
        if (a.Rule == "skip") parts.Add("Can't target the same enemy again before it acts.");
        if (a.MinuteGain > 0) parts.Add($"+{Minutes(a.MinuteGain)}.");
        if (a.HpCost > 0) parts.Add($"Costs {Pct(a.HpCost)} of max HP.");
        if (a.Cooldown > 0) parts.Add($"Once every {a.Cooldown} turns.");
        if (parts.Count == 0)
            parts.Add(a.Intent switch
            {
                Intent.Charge => "Counts down to its finisher.",
                Intent.Recover => "Recovering from the Stagger.",
                _ => "Does nothing.",
            });
        parts.Add(Recovery(a, enemy));
        return string.Join(" ", parts.Where(p => p != ""));
    }

    public static string Status(StatusId s, Tuning t) => s switch
    {
        StatusId.Burn => $"loses {Pct(t.BurnPct)} of max HP (at most {t.BurnCap}) at the start of each turn",
        StatusId.Chill => $"SPD -{Pct(t.ChillSlow)}",
        StatusId.Jolt => $"each hit taken delays it {Pct(t.JoltDelay)} more",
        StatusId.Echo => $"the next hit taken repeats at {Pct(t.EchoMult)}",
        StatusId.Focus => $"deals {Pct(t.FocusMult - 1)} more damage",
        StatusId.Pressure => $"takes {Pct(t.PressureMult - 1)} more damage",
        StatusId.Fatigued => $"SPD -{Pct(t.FatiguedSlow)} and loses {t.FatiguedOt} Overtime per turn",
        _ => $"takes {Pct(1 - t.GuardMult)} less damage",   // Guard
    };

    public static string Passive(string id, Tuning t) => id switch
    {
        "line_cook" => $"Line Cook: Heat skills deal {Pct(t.LineCookMult - 1)} more damage to Burning enemies.",
        "crowd_work" => $"Crowd Work (bond rank 3+): receiving a Shift Change gives {t.CrowdWorkOt} Overtime.",
        "night_watch" => $"Night Watch: takes the first hit aimed at an ally under {Pct(t.NightWatchHp)} HP, once per enemy action.",
        _ => id,
    };

    public static string Pct(double fraction) => (fraction * 100).ToString("0.#", Inv) + "%";

    static string Effect(EffectDef fx, ActionDef a, CombatData data, bool enemy)
    {
        var who = Who(a.Target, enemy);
        switch (fx.Type)
        {
            case EffectType.Delay: return $"Delays {who} by {Pct(fx.Amount)} of a turn.";
            case EffectType.Advance: return $"Moves {who} up by {Pct(fx.Amount)} of a turn.";
            case EffectType.PullToFront: return $"Pulls {who}'s next turn to the front.";
            case EffectType.Heal: return $"Heals {who} for {Pct(fx.Amount)} of max HP.";
            case EffectType.Shield: return $"Shields {who} for {Pct(fx.Amount)} of the user's max HP.";
            case EffectType.Status: return $"{fx.Status} on {who} ({Duration(fx)}): {Status(fx.Status, data.Tuning)}.";
            case EffectType.Cleanse: return $"Removes all statuses from {who}.";
            case EffectType.CleanseDebuffs: return $"Removes debuffs from {who}.";
            case EffectType.Minutes:
                return fx.Amount >= 0 ? $"The party gains {Minutes((int)fx.Amount)}." : $"Steals {Minutes((int)-fx.Amount)} from the party.";
            case EffectType.Summon:
                var name = data.UnitById(fx.Unit!).Name;
                return $"Calls in {("AEIOUaeiou".Contains(name[0]) ? "an" : "a")} {name} (up to {fx.Max}).";
            case EffectType.ExtraAction: return "Act again right away.";
            default: return "";
        }
    }

    static string Duration(EffectDef fx) => fx.Status switch
    {
        StatusId.Guard => "until the next turn",
        StatusId.Echo => "until triggered",
        _ => fx.Turns == 1 ? "1 turn" : $"{fx.Turns} turns",
    };

    static string Recovery(ActionDef a, bool enemy) => a.Weight switch
    {
        Weight.Light => $"Light: {(enemy ? "its" : "your")} next turn comes sooner.",
        Weight.Heavy => $"Heavy: {(enemy ? "its" : "your")} next turn comes later.",
        Weight.None when a.Effects?.Any(e => e.Type == EffectType.ExtraAction) != true => "Doesn't use a turn.",
        _ => "",
    };

    static string Who(TargetType t, bool enemy) => (t, enemy) switch
    {
        (TargetType.Self, false) => "yourself",
        (TargetType.Self, true) => "itself",
        (TargetType.Enemy, false) => "one enemy",
        (TargetType.Enemy, true) => "one party member",
        (TargetType.AllEnemies, false) => "all enemies",
        (TargetType.AllEnemies, true) => "the whole party",
        (TargetType.Ally, false) => "one ally",
        (TargetType.Ally, true) => "one of its allies",
        (TargetType.AllAllies, false) => "every ally",
        _ => "all its allies",
    };

    static string Minutes(int n) => n == 1 ? "1 Minute" : $"{n} Minutes";
}
