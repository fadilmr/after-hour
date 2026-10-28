using System;
using System.Linq;

namespace AfterHour.Combat;

public readonly record struct Hit(int Amount, bool Weak, bool Resist, bool Crit);

/// <summary>damage = Power × ATK × 100/(100 + DEF) × modifiers, so every number on screen can be traced back.</summary>
public static class Damage
{
    /// <param name="rush">Rush: Power comes from Tuning.RushPower and the hit has no element.</param>
    public static Hit Compute(Tuning t, Combatant attacker, Combatant target, ActionDef a, int chainPasses, Random rng, bool rush = false)
    {
        var element = rush ? Element.None : a.Element;
        bool weak = element != Element.None && target.Def.Weak?.Contains(element) == true;
        bool resist = element != Element.None && target.Def.Resist?.Contains(element) == true;
        bool crit = rng.NextDouble() < t.CritChance;   // both rolls always happen, so a seed replays exactly
        double spread = t.SpreadMin + rng.NextDouble() * (t.SpreadMax - t.SpreadMin);

        double dmg = Expected(t, attacker, target, a, chainPasses, rush);
        if (crit) dmg *= t.CritMult;
        dmg *= spread;

        return new Hit(Math.Max(1, (int)Math.Round(dmg, MidpointRounding.AwayFromZero)), weak, resist, crit);
    }

    /// <summary>One hit's damage before the crit and spread rolls: the number the target forecast shows.</summary>
    public static double Expected(Tuning t, Combatant attacker, Combatant target, ActionDef a, int chainPasses, bool rush = false)
    {
        var element = rush ? Element.None : a.Element;
        var power = rush ? t.RushPower : a.Power;
        var def = target.Defense * (1 - (rush ? 0 : a.DefIgnore));
        bool weak = element != Element.None && target.Def.Weak?.Contains(element) == true;
        bool resist = element != Element.None && target.Def.Resist?.Contains(element) == true;

        double dmg = power * attacker.Atk * 100.0 / (100.0 + def);
        if (weak) dmg *= t.WeakMult;
        if (resist) dmg *= t.ResistMult;
        if (target.Body.Staggered) dmg *= t.StaggeredMult;
        if (attacker.Has(StatusId.Focus)) dmg *= t.FocusMult;
        if (target.Has(StatusId.Pressure)) dmg *= t.PressureMult;
        if (target.Has(StatusId.Guard)) dmg *= t.GuardMult;
        dmg *= 1 + t.ChainBonus * Math.Min(chainPasses, t.ChainMaxPasses);
        if (element == Element.Heat && target.Has(StatusId.Burn) && attacker.Def.Passives?.Contains("line_cook") == true)
            dmg *= t.LineCookMult;
        return dmg;
    }
}
