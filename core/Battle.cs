using System;
using System.Collections.Generic;
using System.Linq;

namespace AfterHour.Combat;

/// <summary>One value per box of the battle flow. Only ChooseAction, Reactions and the cut-in window (TurnStart) wait.</summary>
public enum BattlePhase { BattleStart, TurnStart, ChooseAction, RunIntent, Resolve, Reactions, TurnEnd, BattleEnd }

public enum CommandKind { Act, CutIn, Pass, ShiftChange, Rush }

/// <summary>
/// A player decision. Act: Unit uses Action on Target. CutIn: Unit's Overtime (on Target if it needs one).
/// ShiftChange: hand the turn to Unit. Pass: let the enemy act (cut-in window) or end Reactions. Rush: the finisher.
/// </summary>
public sealed record Command(CommandKind Kind, Combatant? Unit = null, ActionDef? Action = null, Combatant? Target = null);

public enum EventKind
{
    TurnStart, Action, Damage, Heal, Shield, Weakness, Resist, Crit, StaggerBreak, Recover, Status,
    StatusEnd, Delay, Advance, Minutes, Overtime, Summon, KO, Intent, ShiftChange, CutIn, Rush, BattleEnd, Log,
}

/// <summary>Something that happened. Text is the battle-log line; Amount depends on the kind (damage, time, Minutes...).</summary>
public sealed record BattleEvent(EventKind Kind, Combatant? Target, double Amount, string Text);

/// <summary>A party member entering battle. BondRank null = Noa: Shift Change always unlocked, no rank perks.</summary>
public sealed record MemberSetup(string Id, int Hp, int Overtime, int? BondRank);

/// <summary>
/// The battle state machine. Run() advances until the player must decide; Submit() applies a decision and runs on.
/// Godot only listens to Emitted and sends Commands.
/// </summary>
public sealed partial class Battle
{
    /// <summary>The intent a Staggered enemy shows while its next turn is spent recovering.</summary>
    public static readonly ActionDef RecoverAction = new("recover", "Recovering", Target: TargetType.Self, Intent: Intent.Recover);

    public Battle(CombatData data, IReadOnlyList<MemberSetup> party, EncounterDef encounter, StartMode mode,
                  Dictionary<string, int> items, ISet<string> known, int seed)
    {
        Data = data;
        Mode = mode;
        Items = items;
        Known = known;
        Rng = new Random(seed);
        foreach (var m in party)
        {
            var c = Combatant.Create(data.UnitById(m.Id), true, data.Tuning);
            c.Body.Hp = Math.Clamp(m.Hp, 0, c.Body.MaxHp);
            c.Overtime = Math.Clamp(m.Overtime, 0, data.Tuning.OtMax);
            c.BondRank = m.BondRank;
            Party.Add(c);
        }
        foreach (var id in encounter.Enemies) Enemies.Add(Spawn(data.UnitById(id)));
    }

    public CombatData Data { get; }
    public StartMode Mode { get; }
    public BattlePhase Phase { get; private set; } = BattlePhase.BattleStart;

    /// <summary>Whose turn it is (ChooseAction, Reactions), or the enemy about to act (cut-in window).</summary>
    public Combatant? Actor { get; private set; }

    public List<Combatant> Party { get; } = new();
    public List<Combatant> Enemies { get; } = new();
    public IEnumerable<Combatant> Living => Party.Concat(Enemies).Where(u => u.Alive);
    public int ChainPasses { get; private set; }
    public int TurnCount { get; private set; }
    public bool Won { get; private set; }

    /// <summary>Total Timeline time passed.</summary>
    public double Clock { get; private set; }

    public Dictionary<string, int> Items { get; }

    /// <summary>Discovered affinities as "bodyOrDefId:Element". Shared with the session so discoveries persist.</summary>
    public ISet<string> Known { get; }

    internal Random Rng { get; }

    public event Action<BattleEvent>? Emitted;

    int _minutes;

    /// <summary>The shared pool, always within 0..MaxMinutes.</summary>
    public int Minutes
    {
        get => _minutes;
        set => _minutes = Math.Clamp(value, 0, Data.Tuning.MaxMinutes);
    }

    Tuning T => Data.Tuning;
    readonly Dictionary<string, Body> _bodies = new();
    readonly HashSet<Combatant> _chain = new();   // members who acted in the current Shift Change chain
    bool _windowPassed;    // the player let the peeked enemy act without cutting in
    ActionReport _last;    // what the current turn's action did, for Reactions

    public static string KnownKey(Combatant u, Element e) => $"{u.Def.Body ?? u.Def.Id}:{e}";

    /// <summary>Advances until the player must decide (ChooseAction, Reactions, cut-in window) or the battle ends.</summary>
    public void Run()
    {
        while (true)
        {
            switch (Phase)
            {
                case BattlePhase.BattleStart: StartBattle(); break;
                case BattlePhase.TurnStart: if (!NextTurn()) return; break;
                case BattlePhase.RunIntent: EnemyTurn(); break;
                case BattlePhase.TurnEnd: EndTurn(); break;
                default: return;
            }
        }
    }

    /// <summary>Applies a decision if it's legal right now, then runs on. False means nothing changed.</summary>
    public bool Submit(Command c)
    {
        bool ok = (Phase, c.Kind) switch
        {
            (BattlePhase.ChooseAction, CommandKind.Act) => Act(c),
            (BattlePhase.ChooseAction or BattlePhase.TurnStart, CommandKind.CutIn) => CutIn(c),
            (BattlePhase.TurnStart, CommandKind.Pass) => LetEnemyAct(),
            (BattlePhase.Reactions, CommandKind.ShiftChange) => ShiftChange(c),
            (BattlePhase.Reactions, CommandKind.Rush) => Rush(),
            (BattlePhase.Reactions, CommandKind.Pass) => EndReactions(),
            _ => false,
        };
        if (ok) Run();
        return ok;
    }

    public bool CanUse(Combatant unit, ActionDef a, out string why)
    {
        why = "";
        if (unit.IsParty && Minutes < a.MinuteCost) why = $"Needs {a.MinuteCost} Minute{(a.MinuteCost == 1 ? "" : "s")}";
        else if (a.HpCost > 0 && unit.Body.Hp <= HpCost(unit, a)) why = $"Needs more than {HpCost(unit, a)} HP";
        else if (unit.Cooldowns.GetValueOrDefault(a.Id) is > 0 and var wait) why = $"Ready in {wait} turn{(wait == 1 ? "" : "s")}";
        else if (IsItem(a) && Items.GetValueOrDefault(a.Id) <= 0) why = "None left";
        else if (a.Target is TargetType.Enemy or TargetType.Ally && Targets(unit, a).Count == 0) why = "No valid target";
        return why == "";
    }

    /// <summary>Who the unit may pick for a single-target action. Empty for Self and All* actions.</summary>
    public IReadOnlyList<Combatant> Targets(Combatant unit, ActionDef a)
    {
        var foes = (unit.IsParty ? Enemies : Party).Where(x => x.Alive);
        var friends = (unit.IsParty ? Party : Enemies).Where(x => x.Alive);
        return a.Target switch
        {
            TargetType.Enemy => foes.Where(x => !(a.Rule == "skip" && x.SkippedSinceTurn)).ToList(),   // no Skip twice before it acts
            TargetType.Ally => friends.Where(x => x != unit || !Pulls(a)).ToList(),                     // Fast-forward can't pick its user
            _ => [],
        };
    }

    static bool Pulls(ActionDef a) => a.Effects?.Any(e => e.Type == EffectType.PullToFront) == true;

    static int HpCost(Combatant u, ActionDef a) => (int)Math.Round(a.HpCost * u.Body.MaxHp, MidpointRounding.AwayFromZero);

    bool IsItem(ActionDef a) => Data.Items.Exists(i => i.Action == a.Id);

    /// <summary>Who can take a Shift Change right now: living members who haven't acted in this chain.</summary>
    public IReadOnlyList<Combatant> ShiftReceivers() => Phase == BattlePhase.Reactions ? Receivers() : [];

    /// <summary>The action just Staggered an enemy, and every enemy still standing is Staggered.</summary>
    public bool RushReady => Phase == BattlePhase.Reactions && RushPossible();

    List<Combatant> Receivers()
    {
        var passer = Actor;
        if (passer == null || !(_last.HitWeakness || _last.Staggered) || !Enemies.Any(e => e.Alive)) return [];
        if (passer.BondRank is { } rank && rank < T.ShiftUnlockRank) return [];   // Noa (no rank) can always pass
        return Party.Where(p => p.Alive && !_chain.Contains(p)).ToList();
    }

    bool RushPossible() => _last.Staggered && Enemies.Where(e => e.Alive).All(e => e.Body.Staggered);

    /// <summary>
    /// The next n turns. With a hypothetical Act, shows the board as it would be after that action: the actor's
    /// recovery, the action's delays and advances, and weakness delays the player already knows about.
    /// </summary>
    public IReadOnlyList<Combatant> Preview(int n = 8, Command? hypothetical = null)
    {
        if (hypothetical is not { Kind: CommandKind.Act, Unit: { } u, Action: { } a } || PartyTargets(u, a, hypothetical.Target) is not { } targets)
            return Timeline.Preview(Living, n);

        var cd = Living.ToDictionary(x => x, x => x.Countdown);
        foreach (var t in targets.Where(cd.ContainsKey))
        {
            foreach (var fx in a.Effects ?? [])
                cd[t] = fx.Type switch
                {
                    EffectType.Delay => cd[t] + fx.Amount * t.TurnLength,
                    EffectType.Advance => Math.Max(0, cd[t] - fx.Amount * t.TurnLength),
                    EffectType.PullToFront => 0,
                    _ => cd[t],
                };
            if (a.Power > 0 && !t.IsParty && t.Def.Weak?.Contains(a.Element) == true && Known.Contains(KnownKey(t, a.Element)))
                cd[t] += Math.Max(0, Math.Min(T.WeakDelay, T.DelayCap - t.WeakDelayTaken)) * t.TurnLength;
        }
        if (a.Weight != Weight.None && a.Effects?.Any(e => e.Type == EffectType.ExtraAction) != true)
            cd[u] = u.TurnLength * T.WeightFactor(a.Weight);
        return Timeline.Preview(Living, n, cd);
    }

    void StartBattle()
    {
        Minutes = T.StartMinutes + (Mode == StartMode.PreEmptive ? T.PreEmptiveMinutes : 0);
        foreach (var u in Living)
        {
            u.Countdown = u.TurnLength;
            if (Mode == StartMode.PreEmptive && !u.IsParty) u.Countdown *= 1 + T.PreEmptiveDelay;
            if (Mode == StartMode.Ambushed && u.IsParty) u.Countdown *= 1 + T.AmbushDelay;
        }
        Emit(EventKind.Log, null, 0, Mode switch
        {
            StartMode.PreEmptive => "Pre-emptive strike! The enemies start delayed.",
            StartMode.Ambushed => "Ambushed! The party starts delayed.",
            _ => "The battle begins.",
        });
        foreach (var e in Enemies) RollIntent(e);
        Phase = CheckEnd() ? BattlePhase.BattleEnd : BattlePhase.TurnStart;
    }

    /// <summary>Starts the next turn on the Timeline. False while the cut-in window waits for the player.</summary>
    bool NextTurn()
    {
        var next = Timeline.Next(Living);
        if (!next.IsParty && !_windowPassed && Party.Any(p => p.Alive && p.Overtime >= T.OtMax))
        {
            Actor = next;
            return false;
        }
        _windowPassed = false;
        var elapsed = next.Countdown;
        Timeline.Pass(Living, elapsed);
        Clock += elapsed;
        ChainPasses = 0;
        _chain.Clear();
        BeginTurn(next);
        return true;
    }

    void BeginTurn(Combatant u)
    {
        Actor = u;
        _last = default;
        if (u.IsParty) _chain.Add(u);
        TurnCount++;
        u.TurnsTaken++;
        u.WeakDelayTaken = 0;
        u.SkippedSinceTurn = false;
        Emit(EventKind.TurnStart, u, Clock, $"{u.Def.Name}'s turn.");
        RemoveStatus(u, StatusId.Guard);
        if (u.Has(StatusId.Burn))
            Hurt(u, (int)Math.Round(u.Body.MaxHp * T.BurnPct, MidpointRounding.AwayFromZero), $"{u.Def.Name} burns");
        if (!u.Alive)
        {
            Phase = CheckEnd() ? BattlePhase.BattleEnd : BattlePhase.TurnStart;
            return;
        }
        if (u.RecoverNextTurn)
        {
            Recover(u);
            Timeline.Reset(u, Weight.Standard);
            Phase = BattlePhase.TurnEnd;
            return;
        }
        Phase = u.IsParty ? BattlePhase.ChooseAction : BattlePhase.RunIntent;
    }

    void EnemyTurn()
    {
        var e = Actor!;
        var a = e.Intent ?? EnemyAi.RollIntent(this, e);
        Phase = BattlePhase.Resolve;
        Resolve(e, a, EnemyTargets(e, a));
        if (Phase == BattlePhase.BattleEnd) return;
        EnemyAi.OnActed(this, e, a);
        Timeline.Reset(e, a.Weight);
        Phase = BattlePhase.TurnEnd;
    }

    List<Combatant> EnemyTargets(Combatant e, ActionDef a)
    {
        var foes = Party.Where(p => p.Alive).ToList();
        var friends = Enemies.Where(x => x.Alive).ToList();
        return a.Target switch
        {
            TargetType.Self => [e],
            TargetType.AllEnemies => foes,
            TargetType.AllAllies => friends,
            TargetType.Ally => [friends[Rng.Next(friends.Count)]],
            _ => [foes[Rng.Next(foes.Count)]],
        };
    }

    bool Act(Command c)
    {
        var u = Actor!;
        var a = c.Action;
        if (a == null || c.Unit != u || !CanUse(u, a, out _)) return false;
        var targets = PartyTargets(u, a, c.Target);
        if (targets == null) return false;

        Pay(u, a);
        Phase = BattlePhase.Resolve;
        _last = Resolve(u, a, targets);
        if (Phase == BattlePhase.BattleEnd) return true;
        if (a.Effects?.Any(e => e.Type == EffectType.ExtraAction) == true)
        {
            Phase = BattlePhase.ChooseAction;   // Double Shift: she acts again now, countdown untouched
            return true;
        }
        Timeline.Reset(u, a.Weight);
        if (a.Weight != Weight.None) GainOvertime(u, T.OtPerAction);
        if (_last.HitWeakness) GainOvertime(u, T.OtPerWeakness);
        Phase = Receivers().Count > 0 || RushPossible() ? BattlePhase.Reactions : BattlePhase.TurnEnd;
        return true;
    }

    /// <summary>A full Overtime gauge cuts in: resolves at once, never touches the user's countdown, earns nothing.</summary>
    bool CutIn(Command c)
    {
        var m = c.Unit;
        if (m == null || !m.IsParty || !m.Alive || m.Overtime < T.OtMax || m.Def.Overtime == null) return false;
        var a = Data.ActionById(m.Def.Overtime);
        var targets = PartyTargets(m, a, c.Target);
        if (targets == null) return false;

        var (phase, actor) = (Phase, Actor);
        GainOvertime(m, -m.Overtime);
        Emit(EventKind.CutIn, m, 0, $"{m.Def.Name} cuts in: {a.Name}!");
        Phase = BattlePhase.Resolve;
        Resolve(m, a, targets);
        if (Phase == BattlePhase.BattleEnd) return true;
        (Phase, Actor) = (phase, actor);   // the window re-peeks the Timeline; a chosen turn carries on
        return true;
    }

    /// <summary>The passer's turn ends here; the receiver's next turn is pulled forward and starts now.</summary>
    bool ShiftChange(Command c)
    {
        var passer = Actor!;
        var r = c.Unit;
        if (r == null || !Receivers().Contains(r)) return false;

        WrapUp(passer);
        ChainPasses++;
        Emit(EventKind.ShiftChange, r, ChainPasses, $"Shift Change! {passer.Def.Name} hands off to {r.Def.Name}.");
        if (passer.BondRank >= 3) AddMinutes(T.ShiftRank3Minutes);
        if (passer.BondRank >= 4) Heal(r, Round(T.ShiftRank4Heal * r.Body.MaxHp));
        if (r.BondRank >= 3 && r.Def.Passives?.Contains("crowd_work") == true) GainOvertime(r, T.CrowdWorkOt);
        r.Countdown = 0;
        BeginTurn(r);
        return true;
    }

    static readonly ActionDef RushAction = new("rush", "Rush");

    /// <summary>Every member hits every enemy body once. Costs nothing and uses no one's turn.</summary>
    bool Rush()
    {
        if (!RushPossible()) return false;
        Emit(EventKind.Rush, null, 0, "Rush! Everyone piles in!");
        _hitOt.Clear();
        foreach (var m in Party.Where(p => p.Alive).ToList())
            foreach (var e in Enemies.Where(x => x.Alive).DistinctBy(x => x.Body).ToList())
            {
                if (!e.Alive) continue;
                Strike(m, e, RushAction, rush: true);
                if (CheckEnd()) return true;
            }
        Phase = BattlePhase.TurnEnd;
        return true;
    }

    void GainOvertime(Combatant u, int amount)
    {
        var before = u.Overtime;
        u.Overtime = Math.Clamp(u.Overtime + amount, 0, T.OtMax);
        if (u.Overtime != before)
            Emit(EventKind.Overtime, u, u.Overtime - before, u.Overtime >= T.OtMax ? $"{u.Def.Name}'s Overtime is ready!" : "");
    }

    void Pay(Combatant u, ActionDef a)
    {
        if (a.MinuteCost > 0) AddMinutes(-a.MinuteCost);
        if (a.HpCost > 0)
        {
            var cost = HpCost(u, a);
            u.Body.Hp -= cost;   // CanUse guarantees she stays above 0
            Emit(EventKind.Damage, u, cost, $"{u.Def.Name} pays {cost} HP.");
        }
        if (a.Cooldown > 0) u.Cooldowns[a.Id] = a.Cooldown;
        if (IsItem(a)) Items[a.Id]--;
    }

    /// <summary>The units a party action hits, or null if the chosen target isn't allowed.</summary>
    List<Combatant>? PartyTargets(Combatant u, ActionDef a, Combatant? chosen) => a.Target switch
    {
        TargetType.Self => [u],
        TargetType.AllEnemies => Enemies.Where(x => x.Alive).DistinctBy(x => x.Body).ToList(),   // a boss body is hit once
        TargetType.AllAllies => Party.Where(p => p.Alive).ToList(),
        _ => chosen != null && Targets(u, a).Contains(chosen) ? [chosen] : null,
    };

    bool LetEnemyAct()
    {
        _windowPassed = true;
        return true;
    }

    bool EndReactions()
    {
        Phase = BattlePhase.TurnEnd;
        return true;
    }

    void EndTurn()
    {
        var u = Actor!;
        if (u.Alive)
        {
            WrapUp(u);
            if (!u.IsParty) RollIntent(u);
        }
        Phase = CheckEnd() ? BattlePhase.BattleEnd : BattlePhase.TurnStart;
    }

    /// <summary>End-of-turn bookkeeping: Fatigued drains Overtime, statuses and cooldowns count down.</summary>
    void WrapUp(Combatant u)
    {
        if (u.Has(StatusId.Fatigued)) GainOvertime(u, -T.FatiguedOt);
        TickStatuses(u);
        foreach (var id in u.Cooldowns.Keys.ToList())
            if (u.Cooldowns[id] > 0) u.Cooldowns[id]--;
    }

    void RollIntent(Combatant e)
    {
        if (!e.Alive) return;
        e.Intent = e.RecoverNextTurn ? RecoverAction : EnemyAi.RollIntent(this, e);
        Emit(EventKind.Intent, e, 0, $"{e.Def.Name} readies {e.Intent.Name}.");
    }

    /// <summary>Ends the battle if one side is down. Runs after every resolution, so a kill mid-action ends it at once.</summary>
    bool CheckEnd()
    {
        if (Phase == BattlePhase.BattleEnd) return true;
        bool enemiesDown = Enemies.All(e => !e.Alive);
        if (!enemiesDown && Party.Any(p => p.Alive)) return false;
        Won = enemiesDown;
        Phase = BattlePhase.BattleEnd;
        Actor = null;
        Emit(EventKind.BattleEnd, null, Won ? 1 : 0, Won ? "Victory!" : "The party falls. The night is lost.");
        return true;
    }

    internal Combatant Spawn(CombatantDef def)
    {
        Body? shared = null;
        if (def.Body is { } key && !_bodies.TryGetValue(key, out shared))
        {
            shared = new Body { Hp = def.MaxHp, MaxHp = def.MaxHp, Stagger = def.MaxStagger, MaxStagger = def.MaxStagger };
            _bodies[key] = shared;
        }
        var u = Combatant.Create(def, false, T, shared);
        EnemyAi.OnSpawn(this, u);
        return u;
    }

    internal void Emit(EventKind kind, Combatant? target, double amount, string text) =>
        Emitted?.Invoke(new BattleEvent(kind, target, amount, text));
}
