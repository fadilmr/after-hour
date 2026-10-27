using System;
using System.Linq;

namespace AfterHour.Combat;

/// <summary>
/// Enemy intents, picked when a turn is queued and shown on the board. Each behavior keeps its state in Body.Counter.
/// Countdown behaviors (deadline, double_shift) treat the LAST entry of Actions as the finisher.
/// </summary>
public static class EnemyAi
{
    public static void OnSpawn(Battle b, Combatant e)
    {
        if (e.Def.Behavior == "deadline") e.Body.Counter = b.Data.Tuning.DeadlineCount;
    }

    public static ActionDef RollIntent(Battle b, Combatant e)
    {
        var moves = e.Def.Actions!.Select(b.Data.ActionById).ToList();
        switch (e.Def.Behavior)
        {
            case "cycle":       // Actions in order, looping
                return moves[e.Body.Counter % moves.Count];
            case "deadline":    // charge while the count is above 0, then the finisher
                return e.Body.Counter > 0 ? moves[0] : moves[^1];
            case "summoner":    // every SummonEvery-th own turn, if there's room
                var summon = moves.First(m => m.Effects?.Any(f => f.Type == EffectType.Summon) == true);
                var attack = moves.First(m => m != summon);
                return (e.Body.Counter + 1) % b.Data.Tuning.SummonEvery == 0 && Usable(b, summon) ? summon : attack;
            default:            // "random": any usable move
                var usable = moves.Where(m => Usable(b, m)).ToList();
                return usable.Count > 0 ? usable[b.Rng.Next(usable.Count)] : moves[0];
        }
    }

    public static void OnActed(Battle b, Combatant e, ActionDef a)
    {
        switch (e.Def.Behavior)
        {
            case "cycle":
            case "summoner":
                e.Body.Counter++;
                break;
            case "deadline":
                e.Body.Counter = a.Id == e.Def.Actions![^1] ? b.Data.Tuning.DeadlineCount : Math.Max(0, e.Body.Counter - 1);
                break;
        }
    }

    /// <summary>Stagger cancels a charged attack or countdown.</summary>
    public static void OnStagger(Battle b, Combatant e)
    {
        switch (e.Def.Behavior)
        {
            case "cycle":
            case "summoner":
                e.Body.Counter = 0;
                break;
            case "deadline":
                e.Body.Counter = b.Data.Tuning.DeadlineCount;
                break;
        }
    }

    /// <summary>A steal needs Minutes in the pool; a summon needs room.</summary>
    static bool Usable(Battle b, ActionDef a) => (a.Effects ?? []).All(fx => fx.Type switch
    {
        EffectType.Minutes when fx.Amount < 0 => b.Minutes > 0,
        EffectType.Summon => b.Enemies.Count(x => x.Alive && x.Def.Id == fx.Unit) < fx.Max,
        _ => true,
    });
}
