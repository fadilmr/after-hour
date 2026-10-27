namespace AfterHour.Combat;

/// <summary>Enemy intents. Stub until the behaviors land: always the first move.</summary>
public static class EnemyAi
{
    public static ActionDef RollIntent(Battle b, Combatant e) => b.Data.ActionById(e.Def.Actions![0]);

    public static void OnActed(Battle b, Combatant e, ActionDef a) { }

    public static void OnStagger(Battle b, Combatant e) { }
}
