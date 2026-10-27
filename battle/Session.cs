using System;
using System.Collections.Generic;
using System.Linq;
using AfterHour.Combat;
using Godot;

namespace AfterHour;

/// <summary>
/// Autoload: everything that outlives one battle. The combat data, the party's HP and Overtime between fights,
/// the shared item stock, discovered weaknesses, bond ranks and the next encounter.
/// </summary>
public partial class Session : Node
{
    public static Session Instance { get; private set; } = null!;

    static readonly string[] Members = ["noa", "rook", "ines", "bram"];

    public CombatData Data { get; private set; } = null!;
    public Dictionary<string, (int Hp, int Overtime)> Party { get; } = new();
    public Dictionary<string, int> BondRanks { get; } = new() { ["rook"] = 2, ["ines"] = 2, ["bram"] = 2 };
    public Dictionary<string, int> Items { get; } = new();
    public HashSet<string> Known { get; } = new();
    public bool UseBram { get; set; }
    public bool RevealAll { get; set; }

    /// <summary>1 = normal pacing, 0 = instant (autoplay).</summary>
    public double AnimSpeed { get; set; } = 1;

    public EncounterDef? Next { get; set; }
    public StartMode Mode { get; set; } = StartMode.Normal;

    /// <summary>Run with `-- --autoplay`: the bot plays every encounter once, prints RESULT lines and quits.</summary>
    public bool Autoplay { get; private set; }

    public int AutoplayIndex { get; set; }

    public IEnumerable<string> Roster => UseBram ? Members : Members.Take(3);

    public override void _Ready()
    {
        Instance = this;
        Data = CombatData.Load(Godot.FileAccess.GetFileAsString("res://data/combat.json"));
        Autoplay = OS.GetCmdlineUserArgs().Contains("--autoplay");
        if (Autoplay) AnimSpeed = 0;
        Rest();
    }

    public List<MemberSetup> Setup() => Roster
        .Select(id => new MemberSetup(id, Party[id].Hp, Party[id].Overtime, BondRanks.TryGetValue(id, out var r) ? r : null))
        .ToList();

    /// <summary>A rest point: full HP and a restocked bag. Overtime is kept.</summary>
    public void Rest()
    {
        foreach (var id in Members)
            Party[id] = (Data.UnitById(id).MaxHp, Party.TryGetValue(id, out var s) ? s.Overtime : 0);
        foreach (var i in Data.Items) Items[i.Action] = i.Start;
    }

    /// <summary>After a win, HP and Overtime carry over (KO'd members get up with 1 HP). A loss restores HP.</summary>
    public void Apply(Battle b)
    {
        foreach (var m in b.Party)
            Party[m.Def.Id] = (b.Won ? Math.Max(1, m.Body.Hp) : m.Body.MaxHp, m.Overtime);
    }
}
