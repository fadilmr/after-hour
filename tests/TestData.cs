using AfterHour.Combat;

static class TestData
{
    static readonly string Json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "combat.json"));

    /// <summary>A fresh copy of the real data each call, so a test can edit stats and tuning freely.</summary>
    public static CombatData Load() => CombatData.Load(Json);
}
