using System;
using System.Collections.Generic;
using System.Linq;

namespace AfterHour.Combat;

/// <summary>HP and Stagger. Heads of one boss share a single Body.</summary>
public sealed class Body
{
    public int Hp, MaxHp, Stagger, MaxStagger;

    /// <summary>Behavior counter: Deadline's countdown, the boss's "Shift ends in N".</summary>
    public int Counter;

    public bool Staggered, ClauseActive;
}

public sealed class StatusInstance
{
    public StatusId Id;
    public int TurnsLeft;
}

/// <summary>One marker on the Timeline: a party member, an enemy, or one head of a boss.</summary>
public sealed class Combatant
{
    public required CombatantDef Def { get; init; }
    public required bool IsParty { get; init; }
    public required Body Body { get; init; }
    public required Tuning Tuning { get; init; }

    // Live stats start from Def; the tuning panel edits them mid-battle.
    public int Atk, Defense, BaseSpd, Overtime, Shield, TurnsTaken;
    public int? BondRank;
    public double Countdown, WeakDelayTaken;
    public ActionDef? Intent;
    /// <summary>Who a single-target intent will hit, picked with the intent so the board can show it.</summary>
    public Combatant? IntentTarget;
    public bool RecoverNextTurn, SkippedSinceTurn;
    public List<StatusInstance> Statuses { get; } = new();
    public Dictionary<string, int> Cooldowns { get; } = new();

    public bool Alive => Body.Hp > 0;

    public bool Has(StatusId s) => Statuses.Any(x => x.Id == s);

    /// <summary>Current speed after slows and the boss's Overtime Clause. Never below 1.</summary>
    public double Spd
    {
        get
        {
            double spd = BaseSpd;
            if (Has(StatusId.Chill)) spd *= 1 - Tuning.ChillSlow;
            if (Has(StatusId.Fatigued)) spd *= 1 - Tuning.FatiguedSlow;
            if (Body.ClauseActive) spd *= 1 + Tuning.ClauseSpd;
            return Math.Max(1, spd);
        }
    }

    public double TurnLength => 10000 / Spd;

    public static Combatant Create(CombatantDef def, bool isParty, Tuning tuning, Body? body = null) => new()
    {
        Def = def,
        IsParty = isParty,
        Tuning = tuning,
        Body = body ?? new Body { Hp = def.MaxHp, MaxHp = def.MaxHp, Stagger = def.MaxStagger, MaxStagger = def.MaxStagger },
        Atk = def.Atk,
        Defense = def.Def,
        BaseSpd = def.Spd,
    };

    public override string ToString() => Def.Name;
}
