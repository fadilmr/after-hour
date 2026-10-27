using System;
using System.Collections.Generic;
using System.Linq;

namespace AfterHour.Combat;

// Action resolution: hits, weakness, Stagger, effects, statuses.
public sealed partial class Battle
{
    readonly record struct ActionReport(bool HitWeakness, bool Staggered);

    /// <summary>Per target: the damage hits, then weakness and Stagger, then effects.</summary>
    ActionReport Resolve(Combatant actor, ActionDef a, List<Combatant> targets)
    {
        Emit(EventKind.Action, actor, 0, $"{actor.Def.Name}: {a.Name}");
        if (a.MinuteGain > 0) AddMinutes(a.MinuteGain);
        bool weak = false, broke = false;
        foreach (var target in targets)
        {
            if (!target.Alive) continue;
            if (a.Power > 0)
                for (int h = 0; h < a.Hits && target.Alive; h++)
                {
                    var (w, b) = Strike(actor, target, a);
                    weak |= w;
                    broke |= b;
                }
            if (CheckEnd()) return new(weak, broke);
            if (target.Alive) ApplyTargetEffects(actor, a, target);
        }
        CheckEnd();
        return new(weak, broke);
    }

    /// <summary>One hit: damage, then weakness bookkeeping, Stagger and the capped weakness delay.</summary>
    (bool weak, bool broke) Strike(Combatant attacker, Combatant target, ActionDef a, bool rush = false)
    {
        var hit = Damage.Compute(T, attacker, target, a, attacker.IsParty ? ChainPasses : 0, Rng, rush);
        if (!rush && a.Element != Element.None && !target.IsParty) Known.Add(KnownKey(target, a.Element));
        if (hit.Weak) Emit(EventKind.Weakness, target, 0, "Weak!");
        if (hit.Resist) Emit(EventKind.Resist, target, 0, "Resisted.");
        if (hit.Crit) Emit(EventKind.Crit, target, 0, "Critical!");
        Hurt(target, hit.Amount, $"{attacker.Def.Name} hits {target.Def.Name}");

        bool broke = false;
        if (hit.Weak && target.Alive)
        {
            var body = target.Body;
            if (!body.Staggered && body.MaxStagger > 0)
            {
                body.Stagger = Math.Max(0, body.Stagger - a.Stagger);
                if (body.Stagger == 0)
                {
                    Break(target);
                    broke = true;
                }
            }
            WeakDelay(target, T.WeakDelay);
        }
        return (hit.Weak, broke);
    }

    void Hurt(Combatant target, int amount, string text)
    {
        target.Body.Hp = Math.Max(0, target.Body.Hp - amount);
        Emit(EventKind.Damage, target, amount, $"{text} for {amount}.");
        if (target.Body.Hp == 0)
            foreach (var u in Party.Concat(Enemies).Where(x => x.Body == target.Body))   // a boss's heads fall together
            {
                u.Intent = null;
                Emit(EventKind.KO, u, 0, $"{u.Def.Name} is down.");
            }
    }

    /// <summary>The bar hit 0: every head of the body is pushed back and spends its next turn recovering.</summary>
    void Break(Combatant target)
    {
        var body = target.Body;
        body.Staggered = true;
        foreach (var head in Enemies.Where(e => e.Body == body && e.Alive))
        {
            Emit(EventKind.Delay, head, Timeline.Delay(head, T.StaggerDelay), $"{head.Def.Name} reels.");
            head.RecoverNextTurn = true;
            head.Intent = RecoverAction;
        }
        EnemyAi.OnStagger(this, target);
        Emit(EventKind.StaggerBreak, target, 0, $"{target.Def.Name} is Staggered!");
    }

    void Recover(Combatant u)
    {
        u.RecoverNextTurn = false;
        if (u.Body.Staggered)
        {
            u.Body.Staggered = false;
            u.Body.Stagger = u.Body.MaxStagger;
        }
        Emit(EventKind.Recover, u, 0, $"{u.Def.Name} recovers.");
    }

    /// <summary>Weakness (and Jolt) delay, capped at DelayCap between two of the target's turns.</summary>
    void WeakDelay(Combatant target, double fraction)
    {
        var f = Math.Min(fraction, T.DelayCap - target.WeakDelayTaken);
        if (f <= 1e-9) return;
        target.WeakDelayTaken += f;
        Emit(EventKind.Delay, target, Timeline.Delay(target, f), $"{target.Def.Name} is pushed back.");
    }

    void AddMinutes(int delta)
    {
        var before = Minutes;
        Minutes += delta;
        if (Minutes != before)
            Emit(EventKind.Minutes, null, Minutes - before, $"Minutes {(Minutes > before ? "+" : "")}{Minutes - before} ({Minutes}).");
    }

    void ApplyTargetEffects(Combatant actor, ActionDef a, Combatant target)
    {
        foreach (var fx in a.Effects ?? [])
            switch (fx.Type)
            {
                case EffectType.Status: AddStatus(target, fx.Status, fx.Turns); break;
            }
    }

    void AddStatus(Combatant u, StatusId id, int turns)
    {
        var s = u.Statuses.Find(x => x.Id == id);
        if (s == null) u.Statuses.Add(new StatusInstance { Id = id, TurnsLeft = turns });
        else s.TurnsLeft = turns;   // re-applying refreshes the duration
        Emit(EventKind.Status, u, turns, $"{u.Def.Name} gains {id}.");
    }

    void RemoveStatus(Combatant u, StatusId id)
    {
        if (u.Statuses.RemoveAll(x => x.Id == id) > 0) Emit(EventKind.StatusEnd, u, 0, $"{u.Def.Name}'s {id} wears off.");
    }

    /// <summary>Turn end: timed statuses count down. Guard and Echo end on their own triggers.</summary>
    void TickStatuses(Combatant u)
    {
        foreach (var s in u.Statuses.ToList())
        {
            if (s.Id is StatusId.Guard or StatusId.Echo) continue;
            if (--s.TurnsLeft <= 0) RemoveStatus(u, s.Id);
        }
    }
}
