using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AfterHour.Combat;

public enum Element { None, Physical, Heat, Frost, Volt, Resonance }

/// <summary>Recovery after acting. None leaves the countdown untouched (Overtime moves, Double Shift).</summary>
public enum Weight { Light, Standard, Heavy, None }

/// <summary>Relative to the actor's side: Enemy means "the other side".</summary>
public enum TargetType { Self, Ally, AllAllies, Enemy, AllEnemies }

public enum Intent { Attack, Heavy, Debuff, Summon, Charge, Stall, Recover }

public enum StatusId { Burn, Chill, Jolt, Echo, Focus, Pressure, Fatigued, Guard }

public enum EffectType { Delay, Advance, PullToFront, Heal, Shield, Status, Cleanse, CleanseDebuffs, Minutes, Summon, ExtraAction }

public enum StartMode { Normal, PreEmptive, Ambushed }

public sealed record EffectDef(EffectType Type, double Amount = 0, StatusId Status = StatusId.Burn, int Turns = 0, string? Unit = null, int Max = 0);

public sealed record ActionDef(
    string Id, string Name, Element Element = Element.None, double Power = 0, int Stagger = 0,
    int MinuteCost = 0, int MinuteGain = 0, Weight Weight = Weight.Standard, TargetType Target = TargetType.Enemy,
    IReadOnlyList<EffectDef>? Effects = null, int Hits = 1, double HpCost = 0, double DefIgnore = 0,
    int Cooldown = 0, Intent Intent = Intent.Attack, string? Rule = null);

/// <param name="Actions">Party: [basic attack, skills...]. Enemy: the moves its Behavior picks from.</param>
/// <param name="Body">Units with the same Body id share HP and Stagger (the two-headed boss).</param>
public sealed record CombatantDef(
    string Id, string Name, int MaxHp, int Atk, int Def, int Spd, int MaxStagger = 0,
    IReadOnlyList<Element>? Weak = null, IReadOnlyList<Element>? Resist = null,
    IReadOnlyList<string>? Actions = null, string? Overtime = null, string Behavior = "random",
    IReadOnlyList<string>? Passives = null, string? Body = null);

public sealed record EncounterDef(string Id, string Name, IReadOnlyList<string> Enemies);

public sealed record ItemDef(string Action, int Start);

/// <summary>Every rule constant from the Combat spec. The tuning panel edits these live.</summary>
public sealed class Tuning
{
    // Recovery weights
    public double WeightLight = 0.75, WeightStandard = 1.0, WeightHeavy = 1.3;

    // Damage modifiers
    public double WeakMult = 1.25, ResistMult = 0.5, StaggeredMult = 1.5, CritChance = 0.05, CritMult = 1.5;
    public double FocusMult = 1.25, PressureMult = 1.2, GuardMult = 0.6, ChainBonus = 0.2, LineCookMult = 1.2;
    public double SpreadMin = 0.95, SpreadMax = 1.05;
    public int ChainMaxPasses = 3;

    // Timeline pushes
    public double WeakDelay = 0.10, StaggerDelay = 0.25, DelayCap = 0.30, JoltDelay = 0.05;

    // Statuses
    public double BurnPct = 0.08, ChillSlow = 0.2, FatiguedSlow = 0.2, EchoMult = 0.5;
    public int FatiguedOt = 10;

    // Minutes
    public int MaxMinutes = 5, StartMinutes = 3;

    // Overtime
    public int OtMax = 100, OtPerAction = 15, OtPerWeakness = 15, OtPerHitTaken = 5, OtPerEnemyActionCap = 15;

    // Encounter starts
    public double PreEmptiveDelay = 0.3, AmbushDelay = 0.3;
    public int PreEmptiveMinutes = 1;

    // Rush, bonds, passives
    public double RushPower = 1.2, ShiftRank4Heal = 0.10, NightWatchHp = 0.30;
    public int ShiftUnlockRank = 2, ShiftRank3Minutes = 1, CrowdWorkOt = 20;

    // Enemy behaviors
    public int DeadlineCount = 3, SummonEvery = 3, ClauseCount = 4;
    public double ClauseThreshold = 0.5, ClauseSpd = 0.2;

    /// <summary>Recovery multiplier. None counts as Standard (only Timeline.Reset treats None specially).</summary>
    public double WeightFactor(Weight w) => w switch
    {
        Weight.Light => WeightLight,
        Weight.Heavy => WeightHeavy,
        _ => WeightStandard,
    };
}

/// <summary>Everything in data/combat.json. Plain lists; lookups are linear scans (the data is small).</summary>
public sealed class CombatData
{
    public Tuning Tuning { get; set; } = new();
    public string Brace { get; set; } = "brace";
    public List<ActionDef> Actions { get; set; } = new();
    public List<CombatantDef> Combatants { get; set; } = new();
    public List<ItemDef> Items { get; set; } = new();
    public List<EncounterDef> Encounters { get; set; } = new();

    public ActionDef ActionById(string id) =>
        Actions.Find(a => a.Id == id) ?? throw new KeyNotFoundException($"No action with id '{id}' in combat.json");

    public CombatantDef UnitById(string id) =>
        Combatants.Find(c => c.Id == id) ?? throw new KeyNotFoundException($"No combatant with id '{id}' in combat.json");

    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        IncludeFields = true,   // Tuning uses public fields
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static CombatData Load(string json) =>
        JsonSerializer.Deserialize<CombatData>(json, Options) ?? throw new InvalidDataException("combat.json is empty");

    public string ToJson() => JsonSerializer.Serialize(this, Options);
}
