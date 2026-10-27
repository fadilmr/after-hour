using System;
using System.Collections.Generic;
using System.Linq;

namespace AfterHour.Combat;

// Action resolution: hits, weakness, Stagger, effects, statuses, passives.
public sealed partial class Battle
{
    readonly record struct ActionReport(bool HitWeakness, bool Staggered);

    static readonly StatusId[] Debuffs = [StatusId.Burn, StatusId.Chill, StatusId.Jolt, StatusId.Echo, StatusId.Pressure, StatusId.Fatigued];

    bool _nightWatchUsed;                                   // once per enemy action
    readonly Dictionary<Combatant, int> _hitOt = new();     // Overtime from hits taken, capped per enemy action

    /// <summary>Per target: the damage hits, then weakness and Stagger, then the Echo repeat, then effects.</summary>
    ActionReport Resolve(Combatant actor, ActionDef a, List<Combatant> targets)
    {
        Emit(EventKind.Action, actor, 0, $"{actor.Def.Name}: {a.Name}");
        _nightWatchUsed = false;
        _hitOt.Clear();
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
        ApplyActionEffects(a);
        CheckEnd();
        return new(weak, broke);
    }

    /// <summary>One hit: damage, weakness bookkeeping, Stagger, the capped weakness/Jolt delay, then Echo.</summary>
    (bool weak, bool broke) Strike(Combatant attacker, Combatant target, ActionDef a, bool rush = false)
    {
        target = NightWatch(attacker, target);
        var chain = attacker.IsParty && !rush ? ChainPasses : 0;   // Rush gets no Shift Change bonus
        var hit = Damage.Compute(T, attacker, target, a, chain, Rng, rush);
        if (!rush && a.Element != Element.None && !target.IsParty) Known.Add(KnownKey(target, a.Element));
        if (hit.Weak) Emit(EventKind.Weakness, target, 0, "Weak!");
        if (hit.Resist) Emit(EventKind.Resist, target, 0, "Resisted.");
        if (hit.Crit) Emit(EventKind.Crit, target, 0, "Critical!");
        Hurt(target, hit.Amount, $"{attacker.Def.Name} hits {target.Def.Name}");
        if (target.IsParty && !attacker.IsParty && target.Alive)
        {
            var got = _hitOt.GetValueOrDefault(target);
            var add = Math.Min(T.OtPerHitTaken, T.OtPerEnemyActionCap - got);
            if (add > 0)
            {
                _hitOt[target] = got + add;
                GainOvertime(target, add);
            }
        }

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
        if (target.Alive && target.Has(StatusId.Jolt)) WeakDelay(target, T.JoltDelay);
        if (target.Alive && target.Has(StatusId.Echo))
        {
            RemoveStatus(target, StatusId.Echo);
            Hurt(target, Round(hit.Amount * T.EchoMult), $"The echo hits {target.Def.Name}");
        }
        return (hit.Weak, broke);
    }

    /// <summary>Bram's Night Watch: the first enemy hit per action on an ally below the threshold goes to him.</summary>
    Combatant NightWatch(Combatant attacker, Combatant target)
    {
        if (attacker.IsParty || !target.IsParty || _nightWatchUsed || target.Body.Hp >= T.NightWatchHp * target.Body.MaxHp)
            return target;
        var guard = Party.Find(p => p != target && p.Alive && p.Def.Passives?.Contains("night_watch") == true);
        if (guard == null) return target;
        _nightWatchUsed = true;
        Emit(EventKind.Log, guard, 0, $"{guard.Def.Name} steps in front of {target.Def.Name}!");
        return guard;
    }

    /// <summary>Shields absorb first. A body at 0 HP takes every head down with it.</summary>
    void Hurt(Combatant target, int amount, string text)
    {
        int blocked = Math.Min(target.Shield, amount);
        target.Shield -= blocked;
        target.Body.Hp = Math.Max(0, target.Body.Hp - (amount - blocked));
        Emit(EventKind.Damage, target, amount - blocked, blocked > 0 ? $"{text} for {amount} ({blocked} blocked)." : $"{text} for {amount}.");
        if (target.Body.Hp == 0)
            foreach (var u in Party.Concat(Enemies).Where(x => x.Body == target.Body))
            {
                u.Intent = null;
                Emit(EventKind.KO, u, 0, $"{u.Def.Name} is down.");
            }
    }

    void Heal(Combatant target, int amount)
    {
        var healed = Math.Min(amount, target.Body.MaxHp - target.Body.Hp);
        target.Body.Hp += healed;
        Emit(EventKind.Heal, target, healed, $"{target.Def.Name} recovers {healed} HP.");
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

    /// <summary>Weakness and Jolt delay, capped at DelayCap between two of the target's turns.</summary>
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

    /// <summary>Effects that land on each target.</summary>
    void ApplyTargetEffects(Combatant actor, ActionDef a, Combatant target)
    {
        if (a.Rule == "skip") target.SkippedSinceTurn = true;
        foreach (var fx in a.Effects ?? [])
            switch (fx.Type)
            {
                case EffectType.Delay:
                    Emit(EventKind.Delay, target, Timeline.Delay(target, fx.Amount), $"{target.Def.Name} is delayed.");
                    break;
                case EffectType.Advance:
                    Emit(EventKind.Advance, target, Timeline.Advance(target, fx.Amount), $"{target.Def.Name} moves up.");
                    break;
                case EffectType.PullToFront:
                    var removed = target.Countdown;
                    target.Countdown = 0;
                    Emit(EventKind.Advance, target, removed, $"{target.Def.Name} jumps to the front.");
                    break;
                case EffectType.Heal:
                    Heal(target, Round(fx.Amount * target.Body.MaxHp));
                    break;
                case EffectType.Shield:
                    target.Shield = Math.Max(target.Shield, Round(fx.Amount * actor.Body.MaxHp));   // never stacks
                    Emit(EventKind.Shield, target, target.Shield, $"{target.Def.Name} is shielded ({target.Shield}).");
                    break;
                case EffectType.Status:
                    AddStatus(target, fx.Status, fx.Turns);
                    break;
                case EffectType.Cleanse:
                    foreach (var s in target.Statuses.ToList()) RemoveStatus(target, s.Id);
                    break;
                case EffectType.CleanseDebuffs:
                    foreach (var s in target.Statuses.Where(s => Debuffs.Contains(s.Id)).ToList()) RemoveStatus(target, s.Id);
                    break;
            }
    }

    /// <summary>Effects that happen once per action, whatever it targeted.</summary>
    void ApplyActionEffects(ActionDef a)
    {
        foreach (var fx in a.Effects ?? [])
            switch (fx.Type)
            {
                case EffectType.Minutes: AddMinutes((int)fx.Amount); break;
                case EffectType.Summon: Summon(fx.Unit!, fx.Max); break;
            }
    }

    void Summon(string unitId, int max)
    {
        if (Enemies.Count(e => e.Alive && e.Def.Id == unitId) >= max) return;
        var u = Spawn(Data.UnitById(unitId));
        u.Countdown = u.TurnLength;
        Enemies.Add(u);
        Emit(EventKind.Summon, u, 0, $"{u.Def.Name} joins the fight.");
        RollIntent(u);
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

    static int Round(double x) => (int)Math.Round(x, MidpointRounding.AwayFromZero);
}
