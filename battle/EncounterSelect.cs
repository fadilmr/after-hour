using System;
using System.Linq;
using AfterHour.Combat;
using Godot;

namespace AfterHour;

/// <summary>The prototype's hub: pick a fight, a start mode and the party, or rest.</summary>
public partial class EncounterSelect : Control
{
    public override void _Ready()
    {
        Theme = Ui.Theme;
        var s = Session.Instance;

        var mode = GetNode<OptionButton>("%Mode");
        foreach (var m in Enum.GetValues<StartMode>())
            mode.AddItem(m switch { StartMode.PreEmptive => "Pre-emptive strike", StartMode.Ambushed => "Ambushed", _ => "Normal start" });
        mode.Selected = (int)s.Mode;
        mode.ItemSelected += i => s.Mode = (StartMode)(int)i;

        var bram = GetNode<CheckBox>("%Bram");
        bram.ButtonPressed = s.UseBram;
        bram.Toggled += on =>
        {
            s.UseBram = on;
            ShowParty();
        };

        GetNode<Button>("%Rest").Pressed += () =>
        {
            s.Rest();
            ShowParty();
        };

        var list = GetNode<VBoxContainer>("%Encounters");
        foreach (var e in s.Data.Encounters)
        {
            var names = string.Join(", ", e.Enemies.Select(id => s.Data.UnitById(id).Name));
            var button = new Button { Text = $"{e.Name}   ({names})", Alignment = HorizontalAlignment.Left };
            button.Pressed += () => Start(e);
            list.AddChild(button);
        }
        ((Button)list.GetChild(0)).GrabFocus();
        ShowParty();

        if (s.Autoplay) Callable.From(AutoplayNext).CallDeferred();
    }

    void ShowParty()
    {
        var s = Session.Instance;
        var lines = s.Roster.Select(id => $"{s.Data.UnitById(id).Name,-6} HP {s.Party[id].Hp,3}/{s.Data.UnitById(id).MaxHp}   Overtime {s.Party[id].Overtime,3}");
        var items = string.Join("   ", s.Data.Items.Select(i => $"{s.Data.ActionById(i.Action).Name} ×{s.Items[i.Action]}"));
        GetNode<Label>("%PartyInfo").Text = string.Join("\n", lines) + "\n" + items;
    }

    void AutoplayNext()
    {
        var s = Session.Instance;
        if (s.AutoplayIndex >= s.Data.Encounters.Count)
        {
            GD.Print("AUTOPLAY DONE");
            GetTree().Quit(0);
            return;
        }
        s.Mode = StartMode.Normal;
        s.Rest();
        Start(s.Data.Encounters[s.AutoplayIndex++]);
    }

    void Start(EncounterDef e)
    {
        Session.Instance.Next = e;
        GetTree().ChangeSceneToFile("res://battle/battle.tscn");
    }
}
