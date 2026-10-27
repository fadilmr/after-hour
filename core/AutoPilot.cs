using System;
using System.Linq;

namespace AfterHour.Combat;

/// <summary>
/// A simple bot that plays the party: heal when someone is low, exploit known-to-data weaknesses, otherwise
/// basic attack. Drives the simulation tests and the --autoplay smoke run.
/// </summary>
public static class AutoPilot
{
    public static Command Choose(Battle b) => b.Phase switch
    {
        BattlePhase.TurnStart => CutInOrPass(b),
        BattlePhase.Reactions => b.RushReady ? new Command(CommandKind.Rush)
            : b.ShiftReceivers() is { Count: > 0 } r ? new Command(CommandKind.ShiftChange, r[0])
            : new Command(CommandKind.Pass),
        BattlePhase.ChooseAction => ChooseAction(b),
        _ => throw new InvalidOperationException($"Nothing to choose in {b.Phase}"),
    };

    static Command CutInOrPass(Battle b)
    {
        var m = b.Party.FirstOrDefault(p => p.Alive && p.Overtime >= b.Data.Tuning.OtMax && p.Def.Overtime != null);
        if (m == null) return new Command(CommandKind.Pass);
        var a = b.Data.ActionById(m.Def.Overtime!);
        var target = a.Target == TargetType.Enemy ? b.Targets(m, a).OrderBy(e => e.Body.Hp).First() : null;
        return new Command(CommandKind.CutIn, m, a, target);
    }

    static Command ChooseAction(Battle b)
    {
        var u = b.Actor!;
        var moves = u.Def.Actions!.Select(b.Data.ActionById).ToList();
        var skills = moves.Skip(1).ToList();
        var items = b.Data.Items.Select(i => b.Data.ActionById(i.Action));

        var hurt = b.Party.Where(p => p.Alive && p.Body.Hp < 0.4 * p.Body.MaxHp).OrderBy(p => p.Body.Hp).FirstOrDefault();
        if (hurt != null)
            foreach (var a in skills.Concat(items).Where(a => a.Target == TargetType.Ally && a.Effects?.Any(f => f.Type == EffectType.Heal) == true))
                if (b.CanUse(u, a, out _) && b.Targets(u, a).Contains(hurt))
                    return new Command(CommandKind.Act, u, a, hurt);

        foreach (var a in skills.Where(a => a.Power > 0 && a.Element != Element.None && b.CanUse(u, a, out _)))
        {
            if (a.Target == TargetType.AllEnemies && b.Enemies.Any(e => e.Alive && Weak(e, a)))
                return new Command(CommandKind.Act, u, a);
            var weakTarget = b.Targets(u, a).Where(e => Weak(e, a)).OrderBy(e => e.Body.Hp).FirstOrDefault();
            if (weakTarget != null) return new Command(CommandKind.Act, u, a, weakTarget);
        }

        var basic = moves[0];
        return new Command(CommandKind.Act, u, basic, b.Targets(u, basic).OrderBy(e => e.Body.Hp).First());
    }

    static bool Weak(Combatant e, ActionDef a) => e.Def.Weak?.Contains(a.Element) == true;
}
