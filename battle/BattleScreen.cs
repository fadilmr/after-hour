using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AfterHour.Combat;
using Godot;

namespace AfterHour;

/// <summary>
/// The gray-box battle screen. Plays the battle's events (log lines, popups, pauses), redraws the board, enemy
/// panels and party cards from the battle state, and turns button presses into Commands. No rules live here.
/// </summary>
public partial class BattleScreen : Control
{
	static readonly Color Sodium = new(1f, 0.6f, 0.24f), Steel = new(0.62f, 0.7f, 0.78f), Gold = new(1f, 0.82f, 0.4f),
		Red = new(1f, 0.35f, 0.3f), Mint = new(0.45f, 0.9f, 0.6f), CardBg = new(0.1f, 0.13f, 0.21f), Faint = new(1f, 1f, 1f, 0.45f);

	static readonly Element[] Elements = [Element.Physical, Element.Heat, Element.Frost, Element.Volt, Element.Resonance];

	readonly Queue<BattleEvent> _queue = new();
	readonly Dictionary<Combatant, Control> _panels = new();
	readonly SystemFont _mono = new() { FontNames = ["Consolas", "Cascadia Mono", "Courier New", "monospace"] };
	Session _s = null!;
	Battle _b = null!;
	EncounterDef _encounter = null!;
	int _startHp, _maxHp;
	TaskCompletionSource<Command>? _pick;

	VBoxContainer _board = null!, _menu = null!, _prompt = null!;
	HBoxContainer _enemies = null!, _party = null!;
	Label _minutes = null!, _banner = null!, _help = null!;
	RichTextLabel _log = null!;
	string _helpDefault = "";

	Tuning T => _s.Data.Tuning;

	/// <summary>The live battle, for the tuning panel.</summary>
	public Battle Battle => _b;

	public override void _Ready()
	{
		Theme = new Theme { DefaultFontSize = 14 };
		_s = Session.Instance;
		_board = GetNode<VBoxContainer>("%Board");
		_menu = GetNode<VBoxContainer>("%Menu");
		_prompt = GetNode<VBoxContainer>("%Prompt");
		_enemies = GetNode<HBoxContainer>("%Enemies");
		_party = GetNode<HBoxContainer>("%Party");
		_minutes = GetNode<Label>("%Minutes");
		_banner = GetNode<Label>("%Banner");
		_help = GetNode<Label>("%Help");
		_log = GetNode<RichTextLabel>("%Log");
		GetNode<Button>("%Continue").Pressed += () => GetTree().ChangeSceneToFile("res://battle/select.tscn");

		_encounter = _s.Next ?? _s.Data.Encounters[0];
		_b = new Battle(_s.Data, _s.Setup(), _encounter, _s.Mode, _s.Items, _s.Known, Random.Shared.Next());
		_b.Emitted += _queue.Enqueue;
		_maxHp = _b.Party.Sum(p => p.Body.MaxHp);
		_startHp = _b.Party.Sum(p => p.Body.Hp);
		_tuning = GetNode<TuningPanel>("%Tuning");
		_tuning.Build(this);
		RunLoop();
	}

	TuningPanel _tuning = null!;

	public override void _UnhandledInput(InputEvent e)
	{
		if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F1 })
		{
			_tuning.Toggle();
			GetViewport().SetInputAsHandled();
		}
	}

	/// <summary>
	/// One turn at a time: play what happened, redraw, then either run the next turn or ask for a decision.
	/// The screen redraws after a turn's events have played, so numbers change after the hits that cause them.
	/// </summary>
	async void RunLoop()
	{
		_b.PauseAfterEachTurn = true;
		_b.Run();
		if (OS.GetCmdlineUserArgs().Contains("--tuning")) _tuning.Toggle();   // start with the panel open
		while (true)
		{
			await PlayEvents();
			if (!IsInsideTree()) return;
			Refresh();
			if (_b.Phase == BattlePhase.BattleEnd)
			{
				Finish();
				return;
			}
			if (_s.Autoplay && _b.TurnCount > 1000)
			{
				GD.PrintErr($"RESULT {_encounter.Id} stuck after 1000 turns");
				GetTree().Quit(2);
				return;
			}
			if (!_b.AwaitingInput)
			{
				_b.Run();
				continue;
			}
			var command = _s.Autoplay ? AutoPilot.Choose(_b) : await AskPlayer();
			if (!IsInsideTree()) return;
			if (!_b.Submit(command))
			{
				GD.PrintErr($"The battle refused {command.Kind} in {_b.Phase}.");
				if (_s.Autoplay)
				{
					GetTree().Quit(1);
					return;
				}
			}
		}
	}

	async Task PlayEvents()
	{
		while (_queue.TryDequeue(out var e))
		{
			if (e.Text != "") _log.AppendText((e.Kind == EventKind.TurnStart ? "\n" : "") + e.Text + "\n");
			if (e.Kind == EventKind.TurnStart) MarkActor(e.Target);
			switch (e.Kind)
			{
				case EventKind.Action or EventKind.CutIn or EventKind.ShiftChange or EventKind.Rush or EventKind.TurnStart:
					_banner.Text = e.Text;
					await Pause(0.35);
					break;
				case EventKind.Damage when e.Amount > 0:
					Popup(e.Target, $"{e.Amount}", e.Target?.IsParty == true ? Red : Colors.White);
					Flash(e.Target);
					break;
				case EventKind.Heal when e.Amount > 0:
					Popup(e.Target, $"+{e.Amount}", Mint);
					break;
				case EventKind.Weakness:
					Popup(e.Target, "WEAK!", Gold);
					break;
				case EventKind.StaggerBreak:
					Popup(e.Target, "STAGGER!", Sodium);
					break;
				case EventKind.KO:
					Popup(e.Target, "DOWN", Red);
					break;
			}
		}
	}

	async Task Pause(double seconds)
	{
		if (_s.AnimSpeed <= 0 || !IsInsideTree()) return;
		await ToSignal(GetTree().CreateTimer(seconds * _s.AnimSpeed), SceneTreeTimer.SignalName.Timeout);
	}

	/// <summary>Redraws everything that shows battle state. Menus are drawn separately, when a choice is needed.</summary>
	public void Refresh()
	{
		ShowBoard();
		_minutes.Text = "MINUTES  " + new string('●', _b.Minutes) + new string('○', Math.Max(0, T.MaxMinutes - _b.Minutes));
		_panels.Clear();
		Fill(_enemies, _b.Enemies.Where(e => e.Alive).Select(EnemyPanel).ToList());
		Fill(_party, _b.Party.Select(PartyCard).ToList());
	}

	// ---------- the departure board ----------

	void ShowBoard(Command? hypothetical = null)
	{
		var times = new List<double>();
		var order = _b.Preview(8, hypothetical, times);
		var highlight = new List<Combatant>();
		if (hypothetical?.Unit is { } unit) highlight.Add(unit);
		if (hypothetical?.Target is { } target) highlight.Add(target);

		var seen = new HashSet<Combatant>();
		Fill(_board, order.Select((u, i) =>
		{
			var tag = u.IsParty ? "" : seen.Add(u) ? IntentTag(u) : "·";
			var name = u.Def.Name.Length > 15 ? u.Def.Name[..15] : u.Def.Name;
			var row = Text($"{i + 1}  {name,-15} {times[i],4:0}  {tag}", highlight.Contains(u) ? Gold : u.IsParty ? Sodium : Steel);
			row.AddThemeFontOverride("font", _mono);
			return (Control)row;
		}).ToList());
	}

	static string IntentTag(Combatant u) => u.Intent?.Intent switch
	{
		Intent.Attack => "ATK",
		Intent.Heavy => "HVY",
		Intent.Debuff => "DBF",
		Intent.Summon => "SUM",
		Intent.Charge => $"CHG {u.Body.Counter}",
		Intent.Stall => "STL",
		Intent.Recover => "REC",
		_ => "",
	};

	// ---------- panels ----------

	Control EnemyPanel(Combatant e)
	{
		var box = new VBoxContainer();
		box.AddChild(Text(e.Def.Name, Steel));
		box.AddChild(Bar(e.Body.Hp, e.Body.MaxHp, Red));
		box.AddChild(Text($"HP {e.Body.Hp}/{e.Body.MaxHp}"));
		if (e.Body.MaxStagger > 0)
			box.AddChild(e.Body.Staggered ? Text("STAGGERED", Sodium) : Bar(e.Body.Stagger, e.Body.MaxStagger, Gold));
		var affinities = new HFlowContainer();
		foreach (var el in Elements)
		{
			var a = Affinity(e, el);
			affinities.AddChild(Text($"{Short(el)} {a} ", a == "WK" ? Gold : a == "?" ? Faint : Steel));
		}
		box.AddChild(affinities);
		box.AddChild(Text($"Next: {e.Intent?.Name ?? "—"}"));
		if (e.Intent != null)
		{
			var what = Text(Describe.Action(e.Intent, _s.Data, enemy: true), Faint);
			what.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			what.AddThemeFontSizeOverride("font_size", 12);
			box.AddChild(what);
		}
		if (Countdown(e) is { } c) box.AddChild(Text(c, Gold));
		if (e.Statuses.Count > 0) box.AddChild(Text(StatusText(e)));
		return Card(box, e, 140);
	}

	Control PartyCard(Combatant m)
	{
		var box = new VBoxContainer();
		box.AddChild(Text(m.Alive ? m.Def.Name : $"{m.Def.Name} (down)", Sodium));
		box.AddChild(Bar(m.Body.Hp, m.Body.MaxHp, Mint));
		box.AddChild(Text($"HP {m.Body.Hp}/{m.Body.MaxHp}" + (m.Shield > 0 ? $"   shield {m.Shield}" : "")));
		box.AddChild(Bar(m.Overtime, T.OtMax, Sodium));
		box.AddChild(Text($"OVERTIME {m.Overtime}/{T.OtMax}" + (m.BondRank is { } r ? $"   bond {r}" : "")));
		if (CanCutIn(m))
		{
			var ot = _s.Data.ActionById(m.Def.Overtime!);
			var cut = new Button { Text = $"CUT IN: {ot.Name}" };
			cut.Pressed += () => OnCutIn(m);
			Hover(cut, null, $"{ot.Name}: {Describe.Action(ot, _s.Data)}");
			box.AddChild(cut);
		}
		if (m.Statuses.Count > 0) box.AddChild(Text(StatusText(m)));
		foreach (var p in m.Def.Passives ?? [])
		{
			var passive = Text(Describe.Passive(p, T), Faint);
			passive.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			passive.AddThemeFontSizeOverride("font_size", 12);
			box.AddChild(passive);
		}
		var card = Card(box, m, 200);
		if (!m.Alive) card.Modulate = Faint;
		return card;
	}

	/// <summary>Only while the battle is actually waiting on the player, never during a turn's playback.</summary>
	bool CanCutIn(Combatant m) => !_s.Autoplay && m.Alive && m.Def.Overtime != null && m.Overtime >= T.OtMax
		&& _b.AwaitingInput && _b.Phase is BattlePhase.ChooseAction or BattlePhase.TurnStart;

	string Affinity(Combatant e, Element el)
	{
		if (!_s.RevealAll && !_b.Known.Contains(Battle.KnownKey(e, el))) return "?";
		return e.Def.Weak?.Contains(el) == true ? "WK" : e.Def.Resist?.Contains(el) == true ? "RS" : "–";
	}

	static string Short(Element el) => el switch { Element.Physical => "Phy", Element.Resonance => "Res", _ => el.ToString() };

	static string? Countdown(Combatant e) => e.Def.Behavior switch
	{
		"deadline" => e.Body.Counter > 0 ? $"Final Notice in {e.Body.Counter}" : "FINAL NOTICE NEXT",
		"double_shift" when e.Body.ClauseActive => $"Shift ends in {e.Body.Counter}",
		_ => null,
	};

	static string StatusText(Combatant u) =>
		string.Join(" · ", u.Statuses.Select(s => s.Id is StatusId.Guard or StatusId.Echo ? $"{s.Id}" : $"{s.Id} {s.TurnsLeft}"));

	PanelContainer Card(Control content, Combatant u, float width)
	{
		var style = new StyleBoxFlat { BgColor = CardBg };
		style.SetCornerRadiusAll(6);
		style.SetContentMarginAll(8);
		var card = new PanelContainer { CustomMinimumSize = new Vector2(width, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
		card.AddThemeStyleboxOverride("panel", style);
		card.AddChild(content);
		_panels[u] = card;
		Outline(style, _b.Actor == u);
		return card;
	}

	/// <summary>Moves the gold "acting" border as soon as a turn starts, before the redraw.</summary>
	void MarkActor(Combatant? actor)
	{
		foreach (var (u, panel) in _panels)
			if (IsInstanceValid(panel) && panel.GetThemeStylebox("panel") is StyleBoxFlat style) Outline(style, u == actor);
	}

	static void Outline(StyleBoxFlat style, bool acting)
	{
		style.BorderColor = acting ? Gold : new Color(1, 1, 1, 0.12f);
		style.SetBorderWidthAll(acting ? 3 : 1);
	}

	static ProgressBar Bar(int value, int max, Color fill)
	{
		var bar = new ProgressBar { MaxValue = Math.Max(1, max), Value = value, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 8) };
		bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = fill });
		bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.1f) });
		return bar;
	}

	static Label Text(string text, Color? color = null)
	{
		var label = new Label { Text = text };
		if (color is { } c) label.AddThemeColorOverride("font_color", c);
		return label;
	}

	static void Fill(Container box, IEnumerable<Control> items)
	{
		foreach (var child in box.GetChildren())
		{
			box.RemoveChild(child);
			child.QueueFree();
		}
		foreach (var item in items) box.AddChild(item);
	}

	// ---------- choices ----------

	Task<Command> AskPlayer()
	{
		_pick = new TaskCompletionSource<Command>(TaskCreationOptions.RunContinuationsAsynchronously);
		ShowChoices();
		return _pick.Task;
	}

	void ShowChoices()
	{
		switch (_b.Phase)
		{
			case BattlePhase.ChooseAction: ShowMainMenu(); break;
			case BattlePhase.Reactions: ShowReactions(); break;
			case BattlePhase.TurnStart: ShowCutInWindow(); break;
		}
	}

	void Choose(Command c)
	{
		Fill(_menu, []);
		Fill(_prompt, []);
		_help.Text = _helpDefault = "";
		_pick?.TrySetResult(c);
	}

	void ShowMainMenu()
	{
		var u = _b.Actor!;
		var moves = u.Def.Actions!.Select(_s.Data.ActionById).ToList();
		var basic = moves[0];
		_help.Text = _helpDefault = "";
		SetMenu($"{u.Def.Name}'s turn",
			Option(u, basic, $"Attack: {basic.Name}  {Cost(basic)}"),
			Link("Skills ▸", () => ShowList(u, "Skills", moves.Skip(1))),
			Option(u, _s.Data.ActionById(_s.Data.Brace), "Brace  (+1 Min, guard)"),
			Link("Items ▸", () => ShowList(u, "Items", _s.Data.Items.Select(i => _s.Data.ActionById(i.Action)))));
	}

	void ShowList(Combatant u, string title, IEnumerable<ActionDef> actions) =>
		SetMenu(title, actions.Select(a => (Control)Option(u, a, $"{a.Name}  {Cost(a)}")).Append(Link("◂ Back", ShowMainMenu)).ToArray());

	string Cost(ActionDef a)
	{
		var parts = new List<string>();
		if (a.MinuteCost > 0) parts.Add($"{a.MinuteCost} Min");
		if (a.MinuteGain > 0) parts.Add($"+{a.MinuteGain} Min");
		if (a.HpCost > 0) parts.Add($"{Describe.Pct(a.HpCost)} HP");
		if (_s.Items.TryGetValue(a.Id, out var n)) parts.Add($"×{n}");
		return parts.Count > 0 ? $"({string.Join(", ", parts)})" : "";
	}

	Button Option(Combatant u, ActionDef a, string label)
	{
		var ok = _b.CanUse(u, a, out var why);
		var button = new Button { Text = label, Disabled = !ok, TooltipText = why, Alignment = HorizontalAlignment.Left };
		button.Pressed += () => Pick(u, a);
		var help = $"{a.Name}: {Describe.Action(a, _s.Data)}" + (ok ? "" : $"  ({why})");
		Hover(button, a.Target is TargetType.Enemy or TargetType.Ally ? null : new Command(CommandKind.Act, u, a), help);
		return button;
	}

	void Pick(Combatant u, ActionDef a)
	{
		if (a.Target is TargetType.Enemy or TargetType.Ally) ShowTargets(u, a, CommandKind.Act);
		else Choose(new Command(CommandKind.Act, u, a));
	}

	void ShowTargets(Combatant u, ActionDef a, CommandKind kind)
	{
		var help = $"{a.Name}: {Describe.Action(a, _s.Data)}";
		var buttons = _b.Targets(u, a).Select(t =>
		{
			var cmd = new Command(kind, u, a, t);
			var button = new Button { Text = $"→ {t.Def.Name}", Alignment = HorizontalAlignment.Left };
			button.Pressed += () => Choose(cmd);
			Hover(button, kind == CommandKind.Act ? cmd : null, help);
			return (Control)button;
		});
		_help.Text = _helpDefault = help;
		SetMenu($"{a.Name}: pick a target", buttons.Append(Link("◂ Back", ShowChoices)).ToArray());
	}

	/// <summary>
	/// Hovering or focusing an option shows what it does in the help line and, for actions, where the actor
	/// and target would land on the board.
	/// </summary>
	void Hover(Button button, Command? cmd, string help)
	{
		void Show()
		{
			_help.Text = help;
			if (cmd != null) ShowBoard(cmd);
		}
		button.MouseEntered += Show;
		button.FocusEntered += Show;
		button.MouseExited += () =>
		{
			_help.Text = _helpDefault;
			if (cmd != null) ShowBoard();
		};
	}

	void ShowReactions()
	{
		var items = new List<Control>();
		if (_b.RushReady)
		{
			var rush = Link("RUSH!  (everyone hits every enemy)", () => Choose(new Command(CommandKind.Rush)));
			Hover(rush, null, $"Rush: every ally hits every enemy for Power {T.RushPower.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture)}, no element. Costs nothing and uses no one's turn.");
			items.Add(rush);
		}
		var bonus = Describe.Pct(T.ChainBonus * Math.Min(_b.ChainPasses + 1, T.ChainMaxPasses));
		foreach (var r in _b.ShiftReceivers())
		{
			var pass = Link($"Shift Change → {r.Def.Name}", () => Choose(new Command(CommandKind.ShiftChange, r)));
			Hover(pass, null, $"Shift Change: {r.Def.Name} acts right now with +{bonus} damage for this chain. Your turn ends.");
			items.Add(pass);
		}
		items.Add(Link("End turn", () => Choose(new Command(CommandKind.Pass))));
		SetPrompt(_b.RushReady ? "Everyone is Staggered!" : "Weak spot! Keep the momentum?", items, focusLast: false);
	}

	void ShowCutInWindow()
	{
		var items = _b.Party.Where(p => p.Alive && p.Def.Overtime != null && p.Overtime >= T.OtMax)
			.Select(m =>
			{
				var ot = _s.Data.ActionById(m.Def.Overtime!);
				var button = Link($"{m.Def.Name}: {ot.Name}", () => OnCutIn(m));
				Hover(button, null, $"{ot.Name}: {Describe.Action(ot, _s.Data)}");
				return (Control)button;
			})
			.Append(Link("Let it play", () => Choose(new Command(CommandKind.Pass))))
			.ToList();
		SetPrompt($"{_b.Actor!.Def.Name} is about to act. Cut in?", items, focusLast: true);
	}

	void OnCutIn(Combatant m)
	{
		var a = _s.Data.ActionById(m.Def.Overtime!);
		if (a.Target is TargetType.Enemy or TargetType.Ally) ShowTargets(m, a, CommandKind.CutIn);
		else Choose(new Command(CommandKind.CutIn, m, a));
	}

	void SetMenu(string title, params Control[] items)
	{
		Fill(_prompt, []);
		Fill(_menu, items.Prepend(Text(title, Gold)).ToList());
		items.OfType<Button>().FirstOrDefault(b => !b.Disabled)?.GrabFocus();
	}

	void SetPrompt(string title, List<Control> items, bool focusLast)
	{
		Fill(_menu, [Text("Waiting on the prompt above.", Faint)]);
		Fill(_prompt, items.Prepend(Text(title, Gold)).ToList());
		var buttons = items.OfType<Button>().ToList();
		(focusLast ? buttons.LastOrDefault() : buttons.FirstOrDefault())?.GrabFocus();
	}

	static Button Link(string label, Action onPressed)
	{
		var button = new Button { Text = label, Alignment = HorizontalAlignment.Left };
		button.Pressed += onPressed;
		return button;
	}

	// ---------- feedback ----------

	void Popup(Combatant? u, string text, Color color)
	{
		if (u == null || _s.AnimSpeed <= 0 || !_panels.TryGetValue(u, out var panel) || !IsInstanceValid(panel)) return;
		var label = Text(text, color);
		label.AddThemeFontSizeOverride("font_size", 22);
		label.ZIndex = 10;
		AddChild(label);
		label.GlobalPosition = panel.GlobalPosition + new Vector2(panel.Size.X / 2 - 16, 4);
		var tween = label.CreateTween();
		tween.TweenProperty(label, "position:y", label.Position.Y - 36, 0.7 * _s.AnimSpeed);
		tween.Parallel().TweenProperty(label, "modulate:a", 0.0, 0.7 * _s.AnimSpeed);
		tween.TweenCallback(Callable.From(label.QueueFree));
	}

	/// <summary>Hit feedback: a red flash. (Container children can't be shaken by position.)</summary>
	void Flash(Combatant? u)
	{
		if (u == null || _s.AnimSpeed <= 0 || !_panels.TryGetValue(u, out var panel) || !IsInstanceValid(panel)) return;
		panel.Modulate = Red;
		panel.CreateTween().TweenProperty(panel, "modulate", u.Alive ? Colors.White : Faint, 0.25 * _s.AnimSpeed);
	}

	void Finish()
	{
		_s.Apply(_b);
		double lost = 100.0 * (_startHp - _b.Party.Sum(p => p.Body.Hp)) / _maxHp;
		if (_s.Autoplay)
		{
			GD.Print($"RESULT {_encounter.Id} {(_b.Won ? "won" : "lost")} turns={_b.TurnCount} partyHpLost={lost:0}%");
			Callable.From(() => GetTree().ChangeSceneToFile("res://battle/select.tscn")).CallDeferred();
			return;
		}
		GetNode<Label>("%ResultText").Text = (_b.Won ? "VICTORY" : "DEFEAT: the night is lost") +
			$"\nTurns: {_b.TurnCount}    Party HP lost: {lost:0}%";
		GetNode<Control>("%Result").Visible = true;
		GetNode<Button>("%Continue").GrabFocus();
	}
}
