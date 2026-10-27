using System;
using System.Linq;
using AfterHour.Combat;
using Godot;

namespace AfterHour;

/// <summary>
/// F1 debug panel: edit rule constants, unit stats, the Minute pool, items and bond ranks while the battle runs.
/// Stat edits also update the session's data, so later battles keep them; "Copy data as JSON" keeps them for good.
/// </summary>
public partial class TuningPanel : PanelContainer
{
    static readonly Color Header = new(1f, 0.6f, 0.24f);

    BattleScreen _screen = null!;
    VBoxContainer _box = null!;

    Session S => Session.Instance;

    /// <summary>Called by the battle screen once its battle exists (children are ready before their parent).</summary>
    public void Build(BattleScreen screen)
    {
        _screen = screen;
        var bg = new StyleBoxFlat { BgColor = new Color(0.04f, 0.05f, 0.09f), BorderColor = Header };
        bg.SetBorderWidthAll(1);
        bg.SetContentMarginAll(8);
        AddThemeStyleboxOverride("panel", bg);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        AddChild(scroll);
        _box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(_box);
        Rebuild();
        Visible = false;
    }

    public void Toggle()
    {
        Visible = !Visible;
        if (Visible) Rebuild();
    }

    void Rebuild()
    {
        foreach (var child in _box.GetChildren())
        {
            _box.RemoveChild(child);
            child.QueueFree();
        }
        var b = _screen.Battle;

        Title("TUNING  (F1 to close)");
        var copied = new Label();
        var copy = new Button { Text = "Copy data as JSON" };
        copy.Pressed += () =>
        {
            DisplayServer.ClipboardSet(S.Data.ToJson());
            copied.Text = "Copied. Paste over data/combat.json to keep it.";
        };
        _box.AddChild(copy);
        _box.AddChild(copied);

        Title("Display");
        var reveal = new CheckBox { Text = "Reveal weaknesses", ButtonPressed = S.RevealAll };
        reveal.Toggled += on =>
        {
            S.RevealAll = on;
            _screen.Refresh();
        };
        _box.AddChild(reveal);
        Row("Anim speed (0 = instant)", S.AnimSpeed, 0, 3, 0.25, v => S.AnimSpeed = v);

        Title("Party pool");
        Row("Minutes", b.Minutes, 0, S.Data.Tuning.MaxMinutes, 1, v => b.Minutes = (int)v);
        foreach (var item in S.Data.Items)
            Row(S.Data.ActionById(item.Action).Name, S.Items[item.Action], 0, 99, 1, v => S.Items[item.Action] = (int)v);
        foreach (var (id, rank) in S.BondRanks.ToList())
            Row($"{S.Data.UnitById(id).Name} bond rank", rank, 1, 5, 1, v =>
            {
                S.BondRanks[id] = (int)v;
                foreach (var m in b.Party.Where(p => p.Def.Id == id)) m.BondRank = (int)v;
            });

        foreach (var u in b.Party.Concat(b.Enemies).Where(u => u.Alive).ToList())
        {
            Title(u.Def.Name);
            Row("HP", u.Body.Hp, 0, 99999, 1, v => u.Body.Hp = (int)v);
            Row("Max HP", u.Body.MaxHp, 1, 99999, 1, v =>
            {
                u.Body.MaxHp = (int)v;
                u.Body.Hp = Math.Min(u.Body.Hp, u.Body.MaxHp);
                Keep(u, d => d with { MaxHp = (int)v });
            });
            Row("ATK", u.Atk, 0, 9999, 1, v =>
            {
                u.Atk = (int)v;
                Keep(u, d => d with { Atk = (int)v });
            });
            Row("DEF", u.Defense, 0, 9999, 1, v =>
            {
                u.Defense = (int)v;
                Keep(u, d => d with { Def = (int)v });
            });
            Row("SPD", u.BaseSpd, 1, 9999, 1, v =>
            {
                u.BaseSpd = (int)v;
                Keep(u, d => d with { Spd = (int)v });
            });
            if (u.IsParty)
                Row("Overtime", u.Overtime, 0, S.Data.Tuning.OtMax, 1, v => u.Overtime = (int)v);
            else
            {
                Row("Stagger", u.Body.Stagger, 0, 9999, 1, v => u.Body.Stagger = (int)v);
                Row("Max Stagger", u.Body.MaxStagger, 0, 9999, 1, v =>
                {
                    u.Body.MaxStagger = (int)v;
                    Keep(u, d => d with { MaxStagger = (int)v });
                });
            }
        }

        Title("Rules");
        var t = S.Data.Tuning;
        foreach (var f in typeof(Tuning).GetFields())
        {
            if (f.FieldType == typeof(double))
                Row(f.Name, (double)f.GetValue(t)!, f.Name.StartsWith("Weight") ? 0.05 : 0, 1000, 0.01, v => f.SetValue(t, v));
            else if (f.FieldType == typeof(int))
                Row(f.Name, (int)f.GetValue(t)!, MinFor(f.Name), 10000, 1, v => f.SetValue(t, (int)v));
        }
    }

    /// <summary>Counts that divide or gate turns can't be 0 (SummonEvery 0 would divide by zero).</summary>
    static int MinFor(string field) => field is "OtMax" or "SummonEvery" or "DeadlineCount" or "ClauseCount" ? 1 : 0;

    /// <summary>Writes a stat change into the session's data, so later battles keep it.</summary>
    void Keep(Combatant u, Func<CombatantDef, CombatantDef> change)
    {
        var defs = S.Data.Combatants;
        var i = defs.FindIndex(d => d.Id == u.Def.Id);
        if (i >= 0) defs[i] = change(defs[i]);
    }

    void Title(string text)
    {
        var label = new Label { Text = text };
        label.AddThemeColorOverride("font_color", Header);
        _box.AddChild(label);
    }

    void Row(string label, double value, double min, double max, double step, Action<double> set)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true });
        var spin = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = value, CustomMinimumSize = new Vector2(110, 0) };
        spin.ValueChanged += v =>
        {
            set(v);
            _screen.Refresh();
        };
        row.AddChild(spin);
        _box.AddChild(row);
    }
}
