using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AfterHour.Combat;
using Godot;

namespace AfterHour;

/// <summary>
/// The battle screen. Plays the battle's events (log lines, popups, stamps, pauses), redraws the departures board,
/// the field (enemies with their intent and plate, the party) and the party chips from the battle state, and turns
/// choices into Commands. Layout follows battle-ui-research.md: each piece sits on what it describes. No rules live here.
/// </summary>
public partial class BattleScreen : Control
{
	const float Floor = 600, EnemyLeft = 700, EnemyWidth = 680, PartyLeft = 1440, PartyWidth = 440;

	readonly Queue<(BattleEvent E, int Hp)> _queue = new();       // each event with its target's HP right after it happened
	readonly Dictionary<Combatant, Control> _views = new();       // shapes on the field, for popups and placing prompts
	readonly Dictionary<Combatant, Control> _holders = new();     // everything drawn for one unit, for dimming and lifting
	readonly Dictionary<Body, int> _shownHp = new();              // HP on screen while a turn plays back; empty = live
	readonly Dictionary<Body, int> _ghostHp = new();              // HP before the hit just shown, drawn as a draining chunk
	readonly HashSet<Combatant> _spotlight = new();               // the actor and its targets during playback; the rest dim
	readonly Dictionary<Combatant, Control> _chipOf = new();
	readonly Dictionary<Combatant, int> _passKeys = new();        // Shift Change partners and their number keys
	Session _s = null!;
	Battle _b = null!;
	EncounterDef _encounter = null!;
	int _startHp, _maxHp;
	TaskCompletionSource<Command>? _pick;

	VBoxContainer _boardRows = null!, _command = null!;
	HBoxContainer _chips = null!, _topBar = null!;
	Control _field = null!, _overlay = null!, _board = null!;
	PanelContainer _commandBox = null!, _logPanel = null!;
	Label _caption = null!, _stamp = null!, _boardSub = null!;
	Label? _descLabel;
	HBoxContainer? _pips;
	RichTextLabel _log = null!;
	Button _speed = null!;
	TuningPanel _tuning = null!;
	Tween? _stampTween;

	Command? _hover;             // the focused option, previewed on the board and the field
	ActionDef? _hoverAction;     // its action, for the Minutes pips even before a target is picked
	bool _rushPreview, _skipRush, _inspect;
	string _desc = "";
	Action? _back, _onRetry;
	Combatant? _cutInActor;
	Combatant? _actor;           // who is acting in the playback
	ActionDef? _action;          // what they're doing; null outside an action (turn start, Rush)
	Control? _callout;
	string _departed = "";       // the last action, for the DEPARTED row
	int _dealt;                  // its total damage

	Tuning T => _s.Data.Tuning;

	/// <summary>The live battle, for the tuning panel.</summary>
	public Battle Battle => _b;

	public override void _Ready()
	{
		Theme = Ui.Theme;
		_s = Session.Instance;
		_boardRows = GetNode<VBoxContainer>("%BoardRows");
		_command = GetNode<VBoxContainer>("%Command");
		_chips = GetNode<HBoxContainer>("%Chips");
		_topBar = GetNode<HBoxContainer>("%TopBar");
		_field = GetNode<Control>("%Field");
		_overlay = GetNode<Control>("%Overlay");
		_board = GetNode<Control>("%Board");
		_commandBox = GetNode<PanelContainer>("%CommandBox");
		_logPanel = GetNode<PanelContainer>("%LogPanel");
		_caption = GetNode<Label>("%Caption");
		_stamp = GetNode<Label>("%Stamp");
		_boardSub = GetNode<Label>("%BoardSub");
		_log = GetNode<RichTextLabel>("%Log");
		_speed = GetNode<Button>("%Speed");

		_encounter = _s.Next ?? _s.Data.Encounters[0];
		Style(GetNode<Label>("%Location"), _encounter.Name.ToUpperInvariant(), Ui.Cond, 16, Ui.Dim);
		Style(GetNode<Label>("%BoardTitle"), null, Ui.CondBold, 28, Ui.Text);
		Style(_boardSub, null, Ui.Cond, 15, Ui.Dim);
		Style(_caption, "", Ui.Semi, 20, Ui.Dim);
		Style(_stamp, "", Ui.CondBold, 96, Ui.Amber);
		_commandBox.AddThemeStyleboxOverride("panel", Ui.Box(Ui.Panel, Ui.Amber, 2, 12, 14, 14));
		_speed.Pressed += CycleSpeed;
		GetNode<Button>("%LogButton").Pressed += () => _logPanel.Visible = !_logPanel.Visible;
		GetNode<Button>("%MenuButton").Pressed += GoToSelect;
		ShowSpeed();

		_b = new Battle(_s.Data, _s.Setup(), _encounter, _s.Mode, _s.Items, _s.Known, Random.Shared.Next());
		_b.Emitted += e => _queue.Enqueue((e, e.Target?.Body.Hp ?? 0));
		_maxHp = _b.Party.Sum(p => p.Body.MaxHp);
		_startHp = _b.Party.Sum(p => p.Body.Hp);
		_tuning = GetNode<TuningPanel>("%Tuning");
		_tuning.Build(this);
		RunLoop();
	}

	static void Style(Label label, string? text, Font font, int size, Color color)
	{
		if (text != null) label.Text = text;
		label.AddThemeFontOverride("font", font);
		label.AddThemeFontSizeOverride("font_size", size);
		label.AddThemeColorOverride("font_color", color);
	}

	/// <summary>Keys: Z choose, X back, Q Overtime, Tab inspect, F speed, L log, Esc menu, 1–3 Shift Change, R retry, F1 tuning.</summary>
	public override void _Input(InputEvent ev)
	{
		if (ev is not InputEventKey { Pressed: true, Echo: false } k || _s.Autoplay) return;
		if (k.Keycode == Key.F1)
		{
			_tuning.Toggle();
			GetViewport().SetInputAsHandled();
			return;
		}
		if (GetViewport().GuiGetFocusOwner() is LineEdit) return;   // typing in the tuning panel
		bool handled = true;
		switch (k.Keycode)
		{
			case Key.Z:
				if (GetViewport().GuiGetFocusOwner() is BaseButton { Disabled: false } focused) focused.EmitSignal(BaseButton.SignalName.Pressed);
				break;
			case Key.X or Key.Backspace when _back != null: _back(); break;
			case Key.Q: TryOvertime(); break;
			case Key.Tab:
				_inspect = !_inspect;
				DrawField();
				SizeDesc();
				ShowDesc();
				break;
			case Key.F: CycleSpeed(); break;
			case Key.L: _logPanel.Visible = !_logPanel.Visible; break;
			case Key.Escape: GoToSelect(); break;
			case Key.R when _onRetry != null: _onRetry(); break;
			case >= Key.Key1 and <= Key.Key9 when _passKeys.FirstOrDefault(p => p.Value == k.Keycode - Key.Key0).Key is { } partner:
				Choose(new Command(CommandKind.ShiftChange, partner));
				break;
			default: handled = false; break;
		}
		if (handled) GetViewport().SetInputAsHandled();
	}

	/// <summary>Normal 1× → Fast 2× → Relaxed 0.75×. AnimSpeed scales durations, so 2× is 0.5.</summary>
	void CycleSpeed()
	{
		_s.AnimSpeed = _s.AnimSpeed switch { 1 => 0.5, 0.5 => 4 / 3.0, _ => 1 };
		ShowSpeed();
	}

	void ShowSpeed() => _speed.Text = $"F  SPEED {(_s.AnimSpeed > 0 ? 1 / _s.AnimSpeed : 1):0.##}×";

	void GoToSelect() => GetTree().ChangeSceneToFile("res://battle/select.tscn");

	/// <summary>
	/// One turn at a time: play what happened, redraw, then either run the next turn or ask for a decision.
	/// The screen redraws after a turn's events have played, so numbers change after the hits that cause them.
	/// </summary>
	async void RunLoop()
	{
		_b.PauseAfterEachTurn = true;
		Snapshot();
		_b.Run();
		if (OS.GetCmdlineUserArgs().Contains("--tuning")) _tuning.Toggle();   // start with the panel open
		while (true)
		{
			await PlayEvents();
			if (!IsInsideTree()) return;
			_shownHp.Clear();
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
				Snapshot();
				_b.Run();
				continue;
			}
			var command = _s.Autoplay ? AutoPilot.Choose(_b) : await AskPlayer();
			if (!IsInsideTree()) return;
			Snapshot();
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

	/// <summary>The screen keeps drawing these HP values until the events that change them play.</summary>
	void Snapshot()
	{
		_shownHp.Clear();
		foreach (var u in _b.Party.Concat(_b.Enemies)) _shownHp[u.Body] = u.Body.Hp;
	}

	int Hp(Combatant u) => _shownHp.TryGetValue(u.Body, out var hp) ? hp : u.Body.Hp;

	/// <summary>
	/// Plays a turn's events as beats (battle-feedback research): hand-off, announce, wind-up, impact, result, board.
	/// The battle state is already final, so HP is drawn from each event's own snapshot, and the board moves last.
	/// </summary>
	async Task PlayEvents()
	{
		bool played = false, strong = false;
		while (_queue.TryDequeue(out var q))
		{
			if (!IsInsideTree()) return;
			var (e, hp) = q;
			if (e.Text != "") _log.AppendText((e.Kind == EventKind.TurnStart ? "\n" : "") + e.Text + "\n");
			switch (e.Kind)
			{
				case EventKind.TurnStart:   // beat 1: hand-off
					played = true;
					EndCallout();
					_caption.Text = e.Text;
					_actor = e.Target;
					_action = null;
					_spotlight.Clear();
					if (_actor != null) _spotlight.Add(_actor);
					Redraw();
					await Pause(0.35);
					break;
				case EventKind.Action:
					played = true;
					await Announce(e);
					break;
				case EventKind.CutIn or EventKind.ShiftChange or EventKind.Rush:
					played = true;
					EndCallout();
					_action = null;
					_caption.Text = e.Text;
					Stamp(e.Kind switch { EventKind.CutIn => "CUT IN", EventKind.Rush => "RUSH!", _ => "SHIFT CHANGE" });
					await Pause(0.5);
					break;
				case EventKind.Damage when e.Amount > 0 && e.Target != null:
					if (_action != null && e.Target != _actor) _dealt += (int)e.Amount;
					await Impact(e.Target, $"{e.Amount}", e.Target.IsParty ? Ui.Danger : Ui.Text, strong, hp);
					strong = false;
					break;
				case EventKind.Heal when e.Amount > 0 && e.Target != null:
					Popup(e.Target, $"+{e.Amount}", Ui.Mint);
					ShowHp(e.Target, hp);
					await Pause(0.25);
					break;
				case EventKind.Weakness:
					strong = true;
					Popup(e.Target, "WEAK!", Ui.Amber);
					break;
				case EventKind.Crit:
					strong = true;
					break;
				case EventKind.StaggerBreak:
					Popup(e.Target, "STAGGERED!", Ui.Amber);
					await Pause(0.5);
					break;
				case EventKind.KO:
					Popup(e.Target, "DOWN", Ui.Danger);
					break;
			}
		}
		if (!played || !IsInsideTree()) return;
		// beat 6: the dim lifts and the board moves, then the loop opens the next menu
		EndCallout();
		_spotlight.Clear();
		_actor = null;
		_action = null;
		_shownHp.Clear();
		Refresh();
		await Pause(0.35);
	}

	/// <summary>Beats 2 and 3: a callout over the actor (who · what → whom), its targets lit, then the wind-up.</summary>
	async Task Announce(BattleEvent e)
	{
		EndCallout();
		_actor = e.Target;
		_action = e.Action;
		var targets = UpcomingTargets();
		_spotlight.UnionWith(targets);
		if (_actor != null) _spotlight.Add(_actor);
		var whom = _action?.Target is TargetType.AllEnemies or TargetType.AllAllies ? "ALL" : targets.FirstOrDefault()?.Def.Name.ToUpperInvariant();
		var line = $"{_actor?.Def.Name.ToUpperInvariant()} · {_action?.Name ?? e.Text}" + (whom != null ? $" → {whom}" : "");
		_departed = (_actor?.IsParty == true ? "● " : "◆ ") + line;
		_dealt = 0;
		_caption.Text = e.Text;
		Redraw();
		ShowCallout(line);
		await Pause(0.7, min: 0.4);
		await WindUp();
	}

	/// <summary>Who the action about to play touches: the targets of its events up to the next turn or action.</summary>
	List<Combatant> UpcomingTargets() => _queue
		.TakeWhile(q => q.E.Kind is not (EventKind.TurnStart or EventKind.Action or EventKind.CutIn or EventKind.ShiftChange or EventKind.Rush))
		.Where(q => q.E.Kind is EventKind.Damage or EventKind.Heal or EventKind.Status or EventKind.Shield or EventKind.Delay or EventKind.Advance)
		.Select(q => q.E.Target)
		.OfType<Combatant>()
		.Where(t => t != _actor)
		.Distinct()
		.ToList();

	void ShowCallout(string line)
	{
		if (_s.AnimSpeed <= 0 || _actor == null || !_views.TryGetValue(_actor, out var view)) return;
		var color = _actor.IsParty ? Ui.Member(_actor.Def.Id) : Ui.Danger;
		// the board's shapes: a round badge for the party, a square one for enemies
		_callout = Ui.Wrap(Ui.L(line, Ui.CondBold, 28, color), Ui.Box(Ui.Ground, color, 2, _actor.IsParty ? 22 : 3, 16, 6));
		_callout.ZIndex = 9;
		AddChild(_callout);
		var size = _callout.GetCombinedMinimumSize();
		_callout.Size = size;
		var top = view.GlobalPosition.Y - size.Y - (_actor.IsParty ? 110 : 70);
		var x = Math.Clamp(view.GlobalPosition.X + view.Size.X / 2 - size.X / 2, 400, Size.X - 40 - size.X);
		_callout.GlobalPosition = new Vector2(x, Math.Max(96, top));
	}

	void EndCallout()
	{
		if (_callout != null && IsInstanceValid(_callout)) _callout.QueueFree();
		_callout = null;
	}

	/// <summary>Enemies stand left of the party, so an enemy lunges right and a member left.</summary>
	static float Toward(Combatant u) => u.IsParty ? -1 : 1;

	static Vector2 Home(Control v) => v.HasMeta("home") ? (Vector2)v.GetMeta("home") : v.Position;

	/// <summary>Beat 3: the actor pulls back and swells, longer for heavier moves.</summary>
	async Task WindUp()
	{
		if (_s.AnimSpeed <= 0 || _actor == null || !_views.TryGetValue(_actor, out var v)) return;
		double t = _action?.Weight switch { Weight.Light => 0.2, Weight.Heavy => 0.45, _ => 0.3 };
		v.SetMeta("home", v.Position);
		v.PivotOffset = v.Size / 2;
		var tw = Anim(v);
		tw.TweenProperty(v, "position:x", v.Position.X - 10 * Toward(_actor), t).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
		tw.Parallel().TweenProperty(v, "scale", Vector2.One * 1.06f, t);
		await ToSignal(tw, Tween.SignalName.Finished);
	}

	/// <summary>Beats 4 and 5: lunge, hit-stop, flash and shake, the number, then the bar snaps with a ghost chunk.</summary>
	async Task Impact(Combatant target, string text, Color color, bool strong, int hp)
	{
		if (_s.AnimSpeed > 0)
		{
			if (_action != null && _actor != null && _actor != target && _views.TryGetValue(_actor, out var a) && IsInstanceValid(a))
			{
				var home = Home(a);
				a.PivotOffset = a.Size / 2;
				var lunge = Anim(a);
				lunge.TweenProperty(a, "position:x", home.X + 24 * Toward(_actor), 0.08).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
				await ToSignal(lunge, Tween.SignalName.Finished);
				if (!IsInstanceValid(a)) return;
				var back = Anim(a);
				back.TweenProperty(a, "position", home, 0.18).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
				back.Parallel().TweenProperty(a, "scale", Vector2.One, 0.18);
			}
			await Pause(strong ? 0.1 : 0.05);   // hit-stop
			Popup(target, text, color);
			Flash(target);
			if (_views.TryGetValue(target, out var v) && IsInstanceValid(v))
			{
				var x = v.Position.X;
				var shake = Anim(v);
				foreach (var dx in new[] { 8f, -6f, 3f, 0f })
					shake.TweenProperty(v, "position:x", x + dx, 0.05).SetTrans(Tween.TransitionType.Sine);
				await ToSignal(shake, Tween.SignalName.Finished);
				if (hp == 0 && !target.IsParty && _holders.TryGetValue(target, out var h) && IsInstanceValid(h))
				{
					var fade = Anim(h);
					fade.TweenProperty(h, "modulate:a", 0.0, 0.3);
					await ToSignal(fade, Tween.SignalName.Finished);
				}
			}
		}
		ShowHp(target, hp);
		await Pause(0.25);
	}

	void ShowHp(Combatant u, int hp)
	{
		_ghostHp[u.Body] = Hp(u);
		_shownHp[u.Body] = hp;
		Redraw();
		_ghostHp.Clear();
	}

	/// <summary>The field and the chips; the board waits for beat 6.</summary>
	void Redraw()
	{
		DrawField();
		DrawChips();
	}

	/// <summary>Every playback tween: Godot runs it at the battle speed, so durations here are the 1× values.</summary>
	Tween Anim(Node n) => CreateTween().BindNode(n).SetSpeedScale((float)(1 / _s.AnimSpeed));

	/// <summary>An HP bar from the shown HP, with the chunk just lost draining away.</summary>
	Control HpBar(Combatant u, int height)
	{
		double max = Math.Max(1, u.Body.MaxHp);
		var bar = Ui.Bar(Hp(u) / max, Ui.HpFill, height, _ghostHp.TryGetValue(u.Body, out var g) ? g / max : 0);
		if (_s.AnimSpeed > 0 && bar.GetNodeOrNull<CanvasItem>("Ghost") is { } ghost)
		{
			var tw = Anim(ghost);
			tw.TweenInterval(0.25);
			tw.TweenProperty(ghost, "modulate:a", 0.0, 0.35);
		}
		return bar;
	}

	async Task Pause(double seconds, double min = 0)
	{
		if (_s.AnimSpeed <= 0 || !IsInsideTree()) return;
		await ToSignal(GetTree().CreateTimer(Math.Max(seconds * _s.AnimSpeed, min)), SceneTreeTimer.SignalName.Timeout);
	}

	/// <summary>Redraws everything that shows battle state. Menus are drawn separately, when a choice is needed.</summary>
	public void Refresh()
	{
		ShowBoard();
		DrawField();
		DrawChips();
		DrawPips();
	}

	/// <summary>The focused option changed: show its outcome on the board, the field and the Minutes pips.</summary>
	void UpdatePreview()
	{
		ShowBoard();
		DrawField();
		DrawPips();
	}

	// ---------- the departures board ----------

	void ShowBoard()
	{
		var hyp = _hover is { Kind: CommandKind.Act or CommandKind.CutIn } ? _hover : null;
		var times = new List<double>();
		var order = _b.Preview(8, hyp, times);
		var before = new Dictionary<Combatant, double>();
		if (hyp != null)
		{
			var baseTimes = new List<double>();
			var baseOrder = _b.Preview(8, null, baseTimes);
			for (int i = 0; i < baseOrder.Count; i++) before.TryAdd(baseOrder[i], baseTimes[i]);
		}
		var marked = hyp != null ? PreviewTargets(hyp).ToHashSet() : new HashSet<Combatant>();
		_boardSub.Text = hyp != null ? $"AFTER {hyp.Action!.Name.ToUpperInvariant()}" : "KEBERANGKATAN BERIKUTNYA";
		_boardSub.AddThemeColorOverride("font_color", hyp != null ? Ui.Text : Ui.Dim);

		var seen = new HashSet<Combatant>();
		Fill(_boardRows, order.Select((u, i) =>
		{
			bool now = i == 0 && (times[0] < 0.5 || (hyp == null && u == _b.Actor));   // the cut-in window: no time has passed yet
			string tag = "";
			var tagColor = Ui.Dim;
			if (seen.Add(u))
			{
				if (hyp != null && u != hyp.Unit && before.TryGetValue(u, out var was))
				{
					var d = times[i] - was;
					if (d > 0.5) (tag, tagColor) = ($"DELAYED +{Ui.Clock(d)}", Ui.Sky);
					else if (d < -0.5) (tag, tagColor) = ($"EARLY −{Ui.Clock(-d)}", Ui.Mint);
				}
				if (tag == "" && !u.IsParty)
				{
					if (u.Intent?.Intent == Intent.Recover) tag = "CANCELLED";
					else if (Countdown(u) is { } c) (tag, tagColor) = (c, Ui.Danger);
				}
			}
			return BoardRow(now ? "NOW" : Ui.Clock(times[i]), u, tag, tagColor, now, marked.Contains(u));
		}).Prepend(hyp == null && _departed != "" ? DepartedRow() : null).OfType<Control>().ToList());
	}

	/// <summary>The last action, so a tester who looked away can still see what happened.</summary>
	Control DepartedRow()
	{
		var what = Ui.L(_departed + (_dealt > 0 ? $" −{_dealt}" : ""), Ui.Cond, 17, Ui.Dim);
		what.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		return Ui.Wrap(Ui.V(0, Ui.L("DEPARTED", Ui.Mono, 12, Ui.Muted), what), Ui.Box(Ui.Row, null, 0, 8, 12, 6));
	}

	static Control BoardRow(string time, Combatant u, string tag, Color tagColor, bool now, bool marked)
	{
		var color = u.IsParty ? Ui.Member(u.Def.Id) : Ui.Text;
		var timeLabel = Ui.L(time, now ? Ui.MonoBold : Ui.Mono, 18, now ? Ui.Ground : Ui.Text);
		timeLabel.CustomMinimumSize = new Vector2(58, 0);
		var dot = Dot(now ? Ui.Ground : u.IsParty ? color : Ui.Danger, 10, u.IsParty ? 5 : 2);
		var name = Ui.L(u.Def.Name.ToUpperInvariant(), Ui.CondBold, 21, now ? Ui.Ground : color);
		name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
		name.ClipText = true;
		var row = Ui.H(10, timeLabel, dot, name);
		if (tag != "") row.AddChild(Ui.L(tag, Ui.CondBold, 15, now ? Ui.Ground : tagColor));
		var panel = Ui.Wrap(row, Ui.Box(now ? Ui.Amber : Ui.Row, marked ? Ui.Text : null, 2, 8, 12, 0));
		panel.CustomMinimumSize = new Vector2(0, 42);
		return panel;
	}

	static Panel Dot(Color color, int size, int radius)
	{
		var dot = new Panel { CustomMinimumSize = new Vector2(size, size), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
		dot.AddThemeStyleboxOverride("panel", Ui.Box(color, radius: radius));
		return dot;
	}

	static string? Countdown(Combatant e) => e.Def.Behavior switch
	{
		"deadline" => e.Body.Counter > 0 ? $"NOTICE IN {e.Body.Counter}" : "FINAL NOTICE",
		"double_shift" when e.Body.ClauseActive => $"SHIFT ENDS {e.Body.Counter}",
		_ => null,
	};

	// ---------- the field ----------

	/// <summary>What the focused option would do to one enemy: the forecast, never the dice.</summary>
	readonly record struct Outcome(double Damage, int HpAfter, int StaggerAfter, string Tags);

	void DrawField()
	{
		Fill(_field, []);
		_views.Clear();
		_holders.Clear();
		var outcomes = Forecast();
		var hypTargets = _hover != null ? PreviewTargets(_hover).ToHashSet() : new HashSet<Combatant>();

		var enemies = _b.Enemies.Where(e => Hp(e) > 0).ToList();
		float slot = EnemyWidth / Math.Max(1, enemies.Count);
		for (int i = 0; i < enemies.Count; i++)
			EnemyView(enemies[i], EnemyLeft + slot * (i + 0.5f), slot, outcomes.TryGetValue(enemies[i], out var o) ? o : null, hypTargets.Contains(enemies[i]));

		float pslot = PartyWidth / Math.Max(1, _b.Party.Count);
		for (int i = 0; i < _b.Party.Count; i++)
			PartySprite(_b.Party[i], PartyLeft + pslot * (i + 0.5f), hypTargets.Contains(_b.Party[i]));

		// during playback: the actor lifts, everyone it doesn't touch dims
		if (_spotlight.Count == 0) return;
		foreach (var (u, holder) in _holders)
		{
			if (!_spotlight.Contains(u)) holder.Modulate *= new Color(1, 1, 1, 0.6f);
			if (u == _actor) holder.Position = new Vector2(0, -12);
		}
	}

	Control Holder(Combatant u)
	{
		var holder = new Control { MouseFilter = MouseFilterEnum.Ignore };
		_field.AddChild(holder);
		_holders[u] = holder;
		return holder;
	}

	Dictionary<Combatant, Outcome> Forecast()
	{
		var result = new Dictionary<Combatant, Outcome>();
		if (_rushPreview)
		{
			foreach (var e in _b.Enemies.Where(x => x.Alive))
			{
				var total = _b.Party.Where(p => p.Alive)
					.Sum(p => Damage.Expected(T, p, e, _s.Data.ActionById(p.Def.Actions![0]), 0, rush: true));
				result[e] = new Outcome(total, Math.Max(0, (int)Math.Round(e.Body.Hp - total)), e.Body.Stagger, "");
			}
			return result;
		}
		if (_hover is not { Kind: CommandKind.Act or CommandKind.CutIn, Unit: { } u, Action: { Power: > 0 } a }) return result;
		foreach (var t in PreviewTargets(_hover).Where(t => !t.IsParty && t.Alive))
		{
			double per = Damage.Expected(T, u, t, a, _b.ChainPasses);
			bool known = _s.RevealAll || _b.Known.Contains(Battle.KnownKey(t, a.Element));
			bool weak = a.Element != Element.None && t.Def.Weak?.Contains(a.Element) == true;
			bool resist = a.Element != Element.None && t.Def.Resist?.Contains(a.Element) == true;
			if (weak && !known) per /= T.WeakMult;       // don't give away an untested weakness
			if (resist && !known) per /= T.ResistMult;
			var tags = new List<string>();
			if (weak && known) tags.Add($"WEAK ×{T.WeakMult:0.##}");
			if (resist && known) tags.Add($"RESIST ×{T.ResistMult:0.##}");
			if (!known && a.Element != Element.None) tags.Add($"{a.Element.ToString().ToUpperInvariant()} ?");
			if (t.Body.Staggered) tags.Add($"STAGGERED ×{T.StaggeredMult:0.##}");
			double total = per * a.Hits;
			int staggerAfter = weak && known && !t.Body.Staggered && t.Body.MaxStagger > 0
				? Math.Max(0, t.Body.Stagger - a.Stagger * a.Hits) : t.Body.Stagger;
			result[t] = new Outcome(total, Math.Max(0, (int)Math.Round(t.Body.Hp - total)), staggerAfter, string.Join(" · ", tags));
		}
		return result;
	}

	List<Combatant> PreviewTargets(Command c) => c.Action?.Target switch
	{
		TargetType.Enemy or TargetType.Ally => c.Target is { } t ? [t] : [],
		TargetType.AllEnemies => _b.Enemies.Where(e => e.Alive).ToList(),
		TargetType.AllAllies => _b.Party.Where(p => p.Alive).ToList(),
		TargetType.Self => c.Unit is { } u ? [u] : [],
		_ => [],
	};

	static (Vector2 Size, bool Round) ShapeOf(CombatantDef d) => d.Id switch
	{
		"rush_hour" => (new(200, 120), false),
		"hold_music" => (new(150, 150), true),
		"deadline" => (new(140, 200), false),
		"endless_meeting" => (new(230, 140), false),
		"insomnia" => (new(130, 190), true),
		"agenda_item" => (new(90, 90), false),
		_ => (new(190, 260), false),
	};

	void EnemyView(Combatant e, float cx, float slot, Outcome? o, bool targeted)
	{
		var (size, round) = ShapeOf(e.Def);
		size *= Math.Min(1f, (slot - 24) / size.X);
		bool acting = e == _cutInActor;
		var holder = Holder(e);
		var shape = new Panel { Position = new Vector2(cx - size.X / 2, Floor - size.Y), Size = size, MouseFilter = MouseFilterEnum.Ignore };
		shape.AddThemeStyleboxOverride("panel", Ui.Box(Ui.Shape, targeted ? Ui.Amber : acting ? Ui.Danger : Ui.Muted, targeted || acting ? 3 : 2,
			round ? (int)(Math.Min(size.X, size.Y) / 2) : 12));
		holder.AddChild(shape);
		_views[e] = shape;

		float top = shape.Position.Y - 10;
		if (IntentText(e) is { Length: > 0 } intent)
		{
			var chip = Place(holder, Ui.Chip(intent, e.Intent!.Intent == Intent.Recover ? Ui.Dim : Ui.Danger, size: 16), cx, top, above: true);
			top = chip.Position.Y - 8;
		}
		if (o is { } f && !_rushPreview)
		{
			var card = Ui.V(2, Ui.L($"≈{Math.Round(f.Damage)}", Ui.MonoBold, 30, Ui.Amber));
			if (f.Tags != "") card.AddChild(Ui.L(f.Tags, Ui.CondBold, 16));
			if (f.StaggerAfter == 0 && e.Body.Stagger > 0) card.AddChild(Ui.L("BREAKS STAGGER", Ui.CondBold, 16, Ui.Amber));
			if (_inspect && _hover?.Action is { } a) card.AddChild(Ui.L($"Power {a.Power:0.0#} · Stagger {a.Stagger}", Ui.Mono, 14, Ui.Dim));
			Place(holder, Ui.Wrap(card, Ui.Box(Ui.Raised, Ui.Amber, 2, 10, 12, 8)), cx, top, above: true);
		}
		Place(holder, Plate(e, Math.Clamp(slot - 16, 170, 240), o, targeted), cx, Floor + 16, above: false);
	}

	/// <summary>One glyph-like word and a number, over the enemy: "ATK ×3 → ROOK", "NOTICE IN 2".</summary>
	static string IntentText(Combatant e)
	{
		if (e.Intent is not { } a) return "";
		if (a.Intent == Intent.Recover) return "REELING";
		if (e.Def.Behavior == "deadline" && a.Intent == Intent.Charge) return $"NOTICE IN {e.Body.Counter}";
		var kind = a.Intent switch
		{
			Intent.Attack => "ATK",
			Intent.Heavy => "HEAVY",
			Intent.Debuff => "DEBUFF",
			Intent.Summon => "SUMMON",
			Intent.Charge => "CHARGE",
			Intent.Stall => "STALL",
			_ => "",
		};
		if (a.Power > 0 && a.Hits > 1) kind += $" ×{a.Hits}";
		if (a.Target == TargetType.AllEnemies) kind += " → ALL";
		else if (e.IntentTarget is { IsParty: true } t) kind += $" → {t.Def.Name.ToUpperInvariant()}";
		return kind;
	}

	Control Plate(Combatant e, float width, Outcome? o, bool targeted)
	{
		var hp = o is { } f ? $"{Hp(e)} → {(f.HpAfter == 0 ? "KO" : f.HpAfter)}" : $"{Hp(e)}";
		var name = Ui.L(e.Def.Name.ToUpperInvariant(), Ui.CondBold, 18);
		name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
		name.ClipText = true;
		var box = Ui.V(6, Ui.H(8, name, Ui.L(hp, Ui.MonoBold, 16, o != null ? Ui.Amber : Ui.Dim)),
			HpBar(e, 6));
		if (e.Body.MaxStagger > 0) box.AddChild(e.Body.Staggered ? Ui.Chip("STAGGERED", Ui.Text) : Pips(e, o));

		var chips = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
		chips.AddThemeConstantOverride("h_separation", 6);
		chips.AddThemeConstantOverride("v_separation", 4);
		foreach (var el in e.Def.Weak ?? [])
			chips.AddChild(Known(e, el) ? Ui.Chip(el.ToString().ToUpperInvariant(), Ui.Of(el)) : Ui.Chip("?", Ui.Muted));
		if (_inspect)
			foreach (var el in (e.Def.Resist ?? []).Where(el => Known(e, el)))
				chips.AddChild(Ui.Chip($"RS {el.ToString().ToUpperInvariant()}", Ui.Dim));
		if (chips.GetChildCount() > 0) box.AddChild(chips);
		if (e.Statuses.Count > 0) box.AddChild(Ui.L(StatusText(e).ToUpperInvariant(), Ui.Cond, 15, Ui.Dim));
		if (_inspect && e.Intent != null)
		{
			var what = Ui.L($"{e.Intent.Name}: {Describe.Action(e.Intent, _s.Data, enemy: true)}", Ui.Body, 14, Ui.Dim);
			what.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			what.CustomMinimumSize = new Vector2(width - 24, 0);
			box.AddChild(what);
		}
		var plate = Ui.Wrap(box, Ui.Box(Ui.Panel, targeted ? Ui.Amber : Ui.Line, targeted ? 2 : 1, 10, 12, 10));
		plate.CustomMinimumSize = new Vector2(width, 0);
		return plate;
	}

	bool Known(Combatant e, Element el) => _s.RevealAll || _b.Known.Contains(Battle.KnownKey(e, el));

	/// <summary>Stagger as pips; a forecast shows the pips it would knock off in amber.</summary>
	static Control Pips(Combatant e, Outcome? o)
	{
		int n = Math.Clamp(e.Body.MaxStagger / 5, 1, 10);
		double per = e.Body.MaxStagger / (double)n;
		int now = (int)Math.Ceiling(e.Body.Stagger / per - 1e-9);
		int after = o is { } f ? (int)Math.Ceiling(f.StaggerAfter / per - 1e-9) : now;
		var row = Ui.H(3);
		for (int i = 0; i < n; i++)
		{
			var pip = new Panel { CustomMinimumSize = new Vector2(0, 8), SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
			pip.AddThemeStyleboxOverride("panel", Ui.Box(i < after ? Ui.Dim : i < now ? Ui.Amber : Ui.Line, radius: 2));
			row.AddChild(pip);
		}
		return row;
	}

	void PartySprite(Combatant m, float cx, bool targeted)
	{
		var color = Ui.Member(m.Def.Id);
		var holder = Holder(m);
		holder.Modulate = Hp(m) > 0 ? Colors.White : Ui.Faint;
		var body = new Panel { Position = new Vector2(cx - 48, Floor - 150), Size = new Vector2(96, 150), MouseFilter = MouseFilterEnum.Ignore };
		var bodyStyle = Ui.Box(new Color("#141C33"), targeted ? Ui.Amber : color, 3, 12);
		bodyStyle.CornerRadiusTopLeft = bodyStyle.CornerRadiusTopRight = 40;
		body.AddThemeStyleboxOverride("panel", bodyStyle);
		var head = new Panel { Position = new Vector2(20, -64), Size = new Vector2(56, 56), MouseFilter = MouseFilterEnum.Ignore };
		head.AddThemeStyleboxOverride("panel", Ui.Box(new Color("#141C33"), targeted ? Ui.Amber : color, 3, 28));
		holder.AddChild(body);
		body.AddChild(head);   // the head rides on the body, so a lunge or shake moves both
		Place(holder, Ui.L(m.Def.Name.ToUpperInvariant(), Ui.CondBold, 20, color), cx, Floor - 150 - 64 - 6, above: true);
		_views[m] = body;
	}

	/// <summary>Adds c to parent, sized to its content, centered on cx, its top (or bottom, when above) at y.</summary>
	static T Place<T>(Control parent, T c, float cx, float y, bool above) where T : Control
	{
		parent.AddChild(c);
		var size = c.GetCombinedMinimumSize();
		c.Size = size;
		c.Position = new Vector2(cx - size.X / 2, above ? y - size.Y : y);
		return c;
	}

	static string StatusText(Combatant u) =>
		string.Join(" · ", u.Statuses.Select(s => s.Id is StatusId.Guard or StatusId.Echo ? $"{s.Id}" : $"{s.Id} {s.TurnsLeft}"));

	// ---------- party chips ----------

	void DrawChips()
	{
		_chipOf.Clear();
		Fill(_chips, _b.Party.Select(Chip).ToList());
	}

	/// <summary>Portrait with the Overtime ring (Q at 100), HP bar, statuses. The active chip is larger, with the only amber border.</summary>
	Control Chip(Combatant m)
	{
		bool active = _b.Actor == m && m.Alive;
		var color = Ui.Member(m.Def.Id);
		double ot = Math.Min(1, m.Overtime / (double)T.OtMax);
		var ring = new Control { CustomMinimumSize = new Vector2(68, 68), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
		ring.Draw += () =>
		{
			var c = new Vector2(34, 34);
			ring.DrawCircle(c, 26, Ui.Raised);
			ring.DrawArc(c, 30, 0, Mathf.Tau, 64, Ui.Line, 5, true);
			if (ot > 0) ring.DrawArc(c, 30, -Mathf.Pi / 2, -Mathf.Pi / 2 + Mathf.Tau * (float)ot, 64, color, 5, true);
			Centered(ring, Ui.CondBold, m.Def.Name[..1], c, 30, color);
			if (ot >= 1 && m.Def.Overtime != null)
			{
				ring.DrawCircle(new Vector2(56, 56), 12, Ui.Amber);
				Centered(ring, Ui.MonoBold, "Q", new Vector2(56, 56), 16, Ui.Ground);
			}
		};
		var top = Ui.H(8, Ui.L(m.Def.Name.ToUpperInvariant(), Ui.CondBold, 22, color), Ui.Spacer(), Ui.L($"{Hp(m)}", Ui.MonoBold, 18));
		if (m.Shield > 0) top.AddChild(Ui.L($"+{m.Shield}", Ui.MonoBold, 16, Ui.Mint));
		var col = Ui.V(6, top, HpBar(m, 8));
		col.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		col.SizeFlagsVertical = SizeFlags.ShrinkCenter;
		if (m.Statuses.Count > 0) col.AddChild(Ui.L(StatusText(m).ToUpperInvariant(), Ui.Cond, 15, Ui.Dim));
		if (_passKeys.TryGetValue(m, out var key)) col.AddChild(Ui.Chip($"{key}  ACT NOW +{ChainBonus()}", Ui.Amber, Ui.Amber));
		var chip = Ui.Wrap(Ui.H(12, ring, col), Ui.Box(Ui.Panel, active ? Ui.Amber : Ui.Line, active ? 2 : 1, 12, 14, active ? 14 : 8));
		chip.CustomMinimumSize = new Vector2(active ? 290 : 250, 0);
		chip.SizeFlagsVertical = SizeFlags.ShrinkEnd;
		if (Hp(m) == 0) chip.Modulate = Ui.Faint;
		_chipOf[m] = chip;
		return chip;
	}

	static void Centered(Control c, Font font, string text, Vector2 center, int size, Color color)
	{
		float baseline = center.Y + (font.GetAscent(size) - font.GetDescent(size)) / 2;
		c.DrawString(font, new Vector2(center.X - 40, baseline), text, HorizontalAlignment.Center, 80, size, color);
	}

	string ChainBonus() => Describe.Pct(T.ChainBonus * Math.Min(_b.ChainPasses + 1, T.ChainMaxPasses));

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
		Fill(_command, []);
		_commandBox.Visible = false;
		Fill(_overlay, []);
		SetHud(true);
		_hover = null;
		_hoverAction = null;
		_rushPreview = _skipRush = false;
		_passKeys.Clear();
		_back = null;
		_cutInActor = null;
		_descLabel = null;
		_pips = null;
		_pick?.TrySetResult(c);
	}

	void ShowMainMenu()
	{
		var u = _b.Actor!;
		var moves = u.Def.Actions!.Select(_s.Data.ActionById).ToList();
		var basic = moves[0];
		var skills = moves.Skip(1).ToList();
		var items = _s.Data.Items.Select(i => _s.Data.ActionById(i.Action)).ToList();
		int ready = skills.Count(a => _b.CanUse(u, a, out _));
		SetMenu($"{u.Def.Name.ToUpperInvariant()} · YOUR MOVE",
		[
			ActRow(u, basic, "Attack", $"{basic.Name} · {basic.Element}"),
			Row("Skills", $"{ready} ready", "", "›", () => ShowList(u, "SKILLS", skills), desc: "Skills cost Minutes from the shared pool."),
			ActRow(u, _s.Data.ActionById(_s.Data.Brace), "Brace", "Guard until your next turn"),
			Row("Items", $"{_s.Items.Values.Sum()} in your bag", "", "›", () => ShowList(u, "ITEMS", items), desc: "Items are shared by the party."),
		], back: null);
	}

	void ShowList(Combatant u, string title, IEnumerable<ActionDef> actions) =>
		SetMenu($"› {title}", actions.Select(a => ActRow(u, a)).ToList(), back: ShowMainMenu);

	Button ActRow(Combatant u, ActionDef a, string? name = null, string? sub = null)
	{
		var ok = _b.CanUse(u, a, out var why);
		var preview = a.Target is TargetType.Enemy or TargetType.Ally ? null : new Command(CommandKind.Act, u, a);
		var desc = Describe.Action(a, _s.Data) + (ok ? "" : $"  ({why})");
		return Row(name ?? a.Name, sub ?? Sub(a), Delta(a), WeightTag(a.Weight), () => Pick(u, a), preview, desc, ok, minutes: a);
	}

	string Sub(ActionDef a)
	{
		var who = a.Target switch
		{
			TargetType.Enemy => "one enemy",
			TargetType.AllEnemies => "every enemy",
			TargetType.Ally => "one ally",
			TargetType.AllAllies => "the party",
			_ => "self",
		};
		if (_s.Items.TryGetValue(a.Id, out var n)) return $"×{n} · {who}";
		return a.Element == Element.None ? who : $"{a.Element} · {who}";
	}

	static string Delta(ActionDef a)
	{
		var parts = new List<string>();
		if (a.MinuteCost > 0) parts.Add($"−{a.MinuteCost}");
		if (a.MinuteGain > 0) parts.Add($"+{a.MinuteGain}");
		if (a.HpCost > 0) parts.Add($"{Describe.Pct(a.HpCost)} HP");
		return string.Join(" ", parts);
	}

	static string WeightTag(Weight w) => w switch { Weight.Light => "LIGHT", Weight.Heavy => "HEAVY", _ => "" };

	void Pick(Combatant u, ActionDef a)
	{
		if (a.Target is TargetType.Enemy or TargetType.Ally) ShowTargets(u, a, CommandKind.Act, ShowChoices);
		else Choose(new Command(CommandKind.Act, u, a));
	}

	void ShowTargets(Combatant u, ActionDef a, CommandKind kind, Action back)
	{
		var desc = $"{a.Name}: {Describe.Action(a, _s.Data)}";
		var rows = _b.Targets(u, a).Select(t =>
		{
			var cmd = new Command(kind, u, a, t);
			var sub = t.IsParty ? $"HP {t.Body.Hp} / {t.Body.MaxHp}" : $"HP {t.Body.Hp}";
			return Row($"→ {t.Def.Name}", sub, "", "", () => Choose(cmd), cmd, desc);
		}).ToList();
		SetMenu($"{a.Name.ToUpperInvariant()} · PICK A TARGET", rows, back);
	}

	void ShowReactions()
	{
		_passKeys.Clear();
		var receivers = _b.ShiftReceivers();
		if (_b.RushReady && !_skipRush)
		{
			ShowRushPrompt(receivers.Count > 0);
			return;
		}
		var bonus = ChainBonus();
		var rows = new List<Button>();
		int key = 1;
		foreach (var r in receivers)
		{
			_passKeys[r] = key;
			rows.Add(Row(r.Def.Name, "Acts now", $"+{bonus}", $"{key++}", () => Choose(new Command(CommandKind.ShiftChange, r)), null,
				$"Shift Change: {r.Def.Name} acts right now with +{bonus} damage for this chain. Their next turn is used now, not added."));
		}
		rows.Add(Row("End turn", "No hand-off", "", "X", () => Choose(new Command(CommandKind.Pass)), null, "End the turn here."));
		DrawChips();
		Stamp("WEAK HIT");
		SetMenu($"SHIFT CHANGE · CHAIN {_b.ChainPasses + 1}", rows, back: () => Choose(new Command(CommandKind.Pass)));
	}

	/// <summary>Every enemy is Staggered: the one screen-wide prompt. The rest of the HUD steps aside.</summary>
	void ShowRushPrompt(bool canHandOff)
	{
		void NotNow()
		{
			if (!canHandOff)
			{
				Choose(new Command(CommandKind.Pass));
				return;
			}
			_skipRush = true;
			Fill(_overlay, []);
			SetHud(true);
			_rushPreview = false;
			ShowReactions();
		}

		SetHud(false);
		Fill(_overlay, []);
		_rushPreview = true;
		var each = Forecast().Values.Select(o => o.Damage).DefaultIfEmpty(0).Average();
		_rushPreview = false;
		var box = Ui.V(10,
			Ui.L("EVERY ENEMY STAGGERED", Ui.CondBold, 52, Ui.Amber),
			Ui.L("Rush: the whole party hits every enemy. Uses no turn and no Minutes.", Ui.Body, 18, Ui.Dim));
		var rush = Row("Rush", $"≈{Math.Round(each)} each", "", "FREE", () => Choose(new Command(CommandKind.Rush)), null, "", rushPreview: true);
		var later = Row("Not now", canHandOff ? "Hand off or end the turn" : "End the turn", "", "X", NotNow);
		box.AddChild(rush);
		box.AddChild(later);
		var panel = Ui.Wrap(box, Ui.Box(Ui.Panel, Ui.Amber, 2, 14, 28, 22));
		panel.CustomMinimumSize = new Vector2(760, 0);
		Place(_overlay, panel, 960, 800, above: false);
		_back = NotNow;
		rush.GrabFocus();
	}

	/// <summary>An enemy is about to act and someone's Overtime is full: a prompt over that enemy.</summary>
	void ShowCutInWindow()
	{
		var actor = _b.Actor!;
		_cutInActor = actor;
		DrawField();
		var rows = CutInRows();
		var pass = Row("Let it play", $"{actor.Def.Name} acts", "", "X", () => Choose(new Command(CommandKind.Pass)));
		rows.Add(pass);
		var box = Ui.V(8,
			Ui.L("CUT IN?", Ui.CondBold, 24, Ui.Amber),
			Ui.L($"{actor.Def.Name} is about to act: {IntentText(actor)}", Ui.Body, 17, Ui.Dim));
		foreach (var r in rows) box.AddChild(r);
		_descLabel = DescLabel(400);
		box.AddChild(_descLabel);
		SizeDesc();
		var panel = Ui.Wrap(box, Ui.Box(Ui.Panel, Ui.Danger, 2, 12, 16, 14));
		panel.CustomMinimumSize = new Vector2(440, 0);
		var view = _views.TryGetValue(actor, out var v) ? v : null;
		float cx = view != null ? Math.Clamp(view.Position.X + view.Size.X / 2, 600, 1300) : 960;
		var placed = Place(_overlay, panel, cx, view?.Position.Y - 60 ?? 400, above: true);
		if (placed.Position.Y < 96) placed.Position = placed.Position with { Y = 96 };
		_back = () => Choose(new Command(CommandKind.Pass));
		pass.GrabFocus();   // the safe default
	}

	List<Combatant> ReadyMembers() => _b.Party.Where(CanCutIn).ToList();

	/// <summary>Only while the battle is actually waiting on the player, never during a turn's playback.</summary>
	bool CanCutIn(Combatant m) => !_s.Autoplay && m.Alive && m.Def.Overtime != null && m.Overtime >= T.OtMax
		&& _b.AwaitingInput && _b.Phase is BattlePhase.ChooseAction or BattlePhase.TurnStart;

	List<Button> CutInRows()
	{
		var ready = ReadyMembers();
		return ready.Select(m =>
		{
			var ot = _s.Data.ActionById(m.Def.Overtime!);
			var preview = ot.Target is TargetType.Enemy or TargetType.Ally ? null : new Command(CommandKind.CutIn, m, ot);
			return Row(m.Def.Name, ot.Name, "", ready.Count == 1 ? "Q" : "", () => OnCutIn(m), preview, $"{ot.Name}: {Describe.Action(ot, _s.Data)}");
		}).ToList();
	}

	void TryOvertime()
	{
		var ready = ReadyMembers();
		if (ready.Count == 1) OnCutIn(ready[0]);
		else if (ready.Count > 1) SetMenu("OVERTIME · CUT IN", CutInRows(), back: ShowChoices);
	}

	void OnCutIn(Combatant m)
	{
		var a = _s.Data.ActionById(m.Def.Overtime!);
		if (a.Target is TargetType.Enemy or TargetType.Ally) ShowTargets(m, a, CommandKind.CutIn, ShowChoices);
		else Choose(new Command(CommandKind.CutIn, m, a));
	}

	// ---------- the command box ----------

	/// <summary>Fills the command box above the active chip: title + Minutes pips, rows, the focused row's description, key hints.</summary>
	void SetMenu(string title, List<Button> rows, Action? back)
	{
		Fill(_overlay, []);
		SetHud(true);
		_back = back;
		_pips = Ui.H(6);
		var header = Ui.H(10, Ui.L(title, Ui.CondBold, 20, Ui.Dim), Ui.Spacer(), _pips);
		var list = Ui.V(4);
		foreach (var r in rows) list.AddChild(r);
		_descLabel = DescLabel(388);
		Fill(_command, [header, list, _descLabel, Hints(back != null)]);
		SizeDesc();
		_commandBox.Visible = true;
		DrawPips();
		(rows.FirstOrDefault(r => !r.Disabled) ?? rows.FirstOrDefault())?.GrabFocus();
		Callable.From(PlaceCommandBox).CallDeferred();
	}

	static Label DescLabel(float width)
	{
		var label = Ui.L("", Ui.Body, 16, Ui.Dim);
		label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		label.VerticalAlignment = VerticalAlignment.Top;
		label.CustomMinimumSize = new Vector2(width, 0);
		return label;
	}

	/// <summary>
	/// The description gets a fixed number of lines (more under Inspect), so hovering rows never resizes the box.
	/// The command box is anchored at its bottom, so any change in height would move the rows under the mouse.
	/// </summary>
	void SizeDesc()
	{
		if (_descLabel == null || !IsInstanceValid(_descLabel)) return;
		int lines = _inspect ? 6 : 3;
		_descLabel.MaxLinesVisible = lines;
		var lineHeight = Ui.Body.GetHeight(16) + _descLabel.GetThemeConstant("line_spacing");
		_descLabel.CustomMinimumSize = _descLabel.CustomMinimumSize with { Y = Mathf.Ceil(lines * lineHeight) };
		Callable.From(PlaceCommandBox).CallDeferred();
	}

	/// <summary>The box sits in the party's column, just above the chips, so it never covers an enemy.</summary>
	void PlaceCommandBox()
	{
		if (!_commandBox.Visible) return;
		_commandBox.Size = new Vector2(_commandBox.CustomMinimumSize.X, 0);
		var anchor = _b.Actor is { IsParty: true } a && _chipOf.TryGetValue(a, out var chip) && IsInstanceValid(chip)
			? chip.GetGlobalRect() : _chips.GetGlobalRect();
		_commandBox.Position = new Vector2(Size.X - 40 - _commandBox.Size.X, anchor.Position.Y - 12 - _commandBox.Size.Y);
	}

	static Control Hints(bool canGoBack)
	{
		static Control Hint(string key, string what) =>
			Ui.H(6, Ui.Wrap(Ui.L(key, Ui.Mono, 14), Ui.Box(Ui.Row, Ui.Muted, 1, 5, 6, 1)), Ui.L(what, Ui.Body, 15, Ui.Dim));
		var row = Ui.H(14, Hint("Z", "Choose"));
		if (canGoBack) row.AddChild(Hint("X", "Back"));
		row.AddChild(Hint("Q", "Overtime"));
		row.AddChild(Hint("Tab", "Inspect"));
		return row;
	}

	/// <summary>One command: name, a short sub-line, the Minutes change and a weight tag. Focus previews it.</summary>
	Button Row(string name, string sub, string delta, string tag, Action onPress, Command? preview = null, string desc = "",
		bool enabled = true, bool rushPreview = false, ActionDef? minutes = null)
	{
		var button = new Button { CustomMinimumSize = new Vector2(0, 42), Disabled = !enabled, FocusMode = FocusModeEnum.All };
		var subLabel = Ui.L(sub, Ui.Body, 16, Ui.Dim);
		subLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		subLabel.ClipText = true;
		var row = Ui.H(12, Ui.L(name, Ui.CondBold, 24), subLabel);
		if (delta != "") row.AddChild(Ui.L(delta, Ui.MonoBold, 17));
		if (tag != "") row.AddChild(Ui.L(tag, Ui.Cond, 15, Ui.Dim));
		button.AddChild(row);
		row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		row.OffsetLeft = 14;
		row.OffsetRight = -14;
		if (!enabled) row.Modulate = Ui.Faint;
		button.Pressed += onPress;
		void Show()
		{
			_hover = preview;
			_hoverAction = minutes ?? preview?.Action;
			_rushPreview = rushPreview;
			_desc = desc;
			ShowDesc();
			UpdatePreview();
		}
		button.FocusEntered += Show;
		button.MouseEntered += () =>
		{
			if (enabled) button.GrabFocus();
			else Show();
		};
		return button;
	}

	void ShowDesc()
	{
		if (_descLabel == null || !IsInstanceValid(_descLabel)) return;
		var text = _desc;
		if (_inspect && _b.Actor is { IsParty: true } u)
			text = string.Join("\n", (u.Def.Passives ?? []).Select(p => Describe.Passive(p, T)).Prepend(text).Where(s => s != ""));
		_descLabel.Text = text;
	}

	/// <summary>Minutes pips; a preview shows spent pips hollow and gained ones half-lit.</summary>
	void DrawPips()
	{
		if (_pips == null || !IsInstanceValid(_pips)) return;
		int now = _b.Minutes, after = now;
		if (_b.Phase == BattlePhase.ChooseAction && _hoverAction is { } a) after = Math.Clamp(now - a.MinuteCost + a.MinuteGain, 0, T.MaxMinutes);
		Fill(_pips, Enumerable.Range(0, T.MaxMinutes).Select(i =>
		{
			var style = i < Math.Min(now, after) ? Ui.Box(Ui.Amber, radius: 9)
				: i < now ? Ui.Box(Colors.Transparent, Ui.Amber, 2, 9)
				: i < after ? Ui.Box(Ui.Amber with { A = 0.45f }, radius: 9)
				: Ui.Box(Colors.Transparent, Ui.Muted, 2, 9);
			var pip = Dot(Colors.White, 18, 9);
			pip.AddThemeStyleboxOverride("panel", style);
			return (Control)pip;
		}).ToList());
	}

	void SetHud(bool on)
	{
		_board.Visible = _chips.Visible = _topBar.Visible = on;
		if (!on) _commandBox.Visible = false;
	}

	static void Fill(Control box, IEnumerable<Control> items)
	{
		foreach (var child in box.GetChildren())
		{
			box.RemoveChild(child);
			child.QueueFree();
		}
		foreach (var item in items) box.AddChild(item);
	}

	// ---------- feedback ----------

	void Stamp(string text, Color? color = null)
	{
		if (_s.AnimSpeed <= 0) return;
		_stampTween?.Kill();
		_stamp.Text = text;
		_stamp.AddThemeColorOverride("font_color", color ?? Ui.Amber);
		_stamp.Visible = true;
		_stamp.Modulate = Colors.Transparent;
		_stamp.Scale = new Vector2(1.15f, 1.15f);
		var speed = _s.AnimSpeed;
		_stampTween = _stamp.CreateTween();
		_stampTween.TweenProperty(_stamp, "modulate", Colors.White, 0.12 * speed);
		_stampTween.Parallel().TweenProperty(_stamp, "scale", Vector2.One, 0.12 * speed);
		_stampTween.TweenInterval(0.5 * speed);
		_stampTween.TweenProperty(_stamp, "modulate", Colors.Transparent, 0.3 * speed);
		_stampTween.TweenCallback(Callable.From(() => _stamp.Visible = false));
	}

	void Popup(Combatant? u, string text, Color color)
	{
		if (u == null || _s.AnimSpeed <= 0 || !_views.TryGetValue(u, out var view) || !IsInstanceValid(view)) return;
		var label = Ui.L(text, Ui.MonoBold, 34, color);
		label.AddThemeConstantOverride("outline_size", 8);
		label.AddThemeColorOverride("font_outline_color", Ui.Ground);
		label.ZIndex = 10;
		AddChild(label);
		label.Size = label.GetCombinedMinimumSize();
		label.GlobalPosition = view.GlobalPosition + new Vector2((view.Size.X - label.Size.X) / 2, -label.Size.Y);
		var tween = label.CreateTween();
		tween.TweenProperty(label, "position:y", label.Position.Y - 44, 0.7 * _s.AnimSpeed);
		tween.Parallel().TweenProperty(label, "modulate:a", 0.0, 0.7 * _s.AnimSpeed);
		tween.TweenCallback(Callable.From(label.QueueFree));
	}

	/// <summary>Hit feedback: one white flash on the shape.</summary>
	void Flash(Combatant? u)
	{
		if (u == null || _s.AnimSpeed <= 0 || !_views.TryGetValue(u, out var view) || !IsInstanceValid(view)) return;
		view.Modulate = new Color(2.5f, 2.5f, 2.5f);
		view.CreateTween().TweenProperty(view, "modulate", Colors.White, 0.25 * _s.AnimSpeed);
	}

	// ---------- how a battle ends ----------

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
		SetHud(false);
		_caption.Text = "";
		Fill(_overlay, []);
		if (_b.Won) Victory(lost);
		else Defeat();
	}

	Control Screen(Color bg)
	{
		var screen = new Panel { MouseFilter = MouseFilterEnum.Stop };
		screen.AddThemeStyleboxOverride("panel", Ui.Box(bg, radius: 0));
		_overlay.AddChild(screen);
		screen.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		return screen;
	}

	static T At<T>(Control parent, T c, float x, float y, float width = 0) where T : Control
	{
		parent.AddChild(c);
		if (width > 0) c.CustomMinimumSize = c.CustomMinimumSize with { X = width };
		c.Position = new Vector2(x, y);
		c.Size = c.GetCombinedMinimumSize();
		return c;
	}

	void Victory(double lost)
	{
		var screen = Screen(Ui.Ground);
		At(screen, Ui.V(12,
			Ui.L(_encounter.Name.ToUpperInvariant(), Ui.Cond, 22, Ui.Dim),
			Ui.L("CLEARED", Ui.CondBold, 150, Ui.Amber),
			Ui.L($"{_b.TurnCount} turns · {Ui.Clock(_b.Clock)} battle time · {lost:0}% party HP lost", Ui.Mono, 22, Ui.Dim)), 120, 120);
		var note = At(screen, Ui.L($"HP and Overtime carry over to the next fight. Minutes reset to {T.StartMinutes}.", Ui.Body, 20, Ui.Dim), 124, 440, 680);
		note.AutowrapMode = TextServer.AutowrapMode.WordSmart;

		var party = Ui.V(16);
		foreach (var m in _b.Party)
		{
			var color = Ui.Member(m.Def.Id);
			var (hp, ot) = _s.Party[m.Def.Id];
			party.AddChild(Ui.Wrap(Ui.V(12,
				Ui.L(m.Def.Name.ToUpperInvariant(), Ui.CondBold, 32, color),
				StatRow("HP", hp / (double)m.Body.MaxHp, $"{hp} / {m.Body.MaxHp}", Ui.HpFill, 12),
				StatRow("OVERTIME", Math.Min(1, ot / (double)T.OtMax), $"{ot}", color, 10)), Ui.Box(Ui.Panel, Ui.Line, 1, 12, 22, 18)));
		}
		At(screen, party, 900, 120, 900);
		var go = BigButton("CONTINUE   Z", primary: true, GoToSelect);
		At(screen, go, 1920 - 120 - go.GetCombinedMinimumSize().X, 930);
		go.GrabFocus();
	}

	static Control StatRow(string label, double frac, string value, Color fill, int height)
	{
		var name = Ui.L(label, Ui.Cond, 16, Ui.Dim);
		name.CustomMinimumSize = new Vector2(110, 0);
		var bar = Ui.Bar(frac, fill, height);
		bar.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		bar.SizeFlagsVertical = SizeFlags.ShrinkCenter;
		var number = Ui.L(value, Ui.MonoBold, 18);
		number.CustomMinimumSize = new Vector2(150, 0);
		number.HorizontalAlignment = HorizontalAlignment.Right;
		return Ui.H(14, name, bar, number);
	}

	void Defeat()
	{
		var screen = Screen(new Color("#07090F"));
		var line = Ui.L("The night is gone. Your next dive starts from the last rest point.", Ui.Body, 28, Ui.Dim);
		At(screen, Ui.V(18,
			Ui.L(_encounter.Name.ToUpperInvariant(), Ui.Mono, 20, Ui.Dim),
			Ui.L("You wake at your desk.", Ui.Semi, 84),
			line), 160, 170, 1300);
		_onRetry = () => GetTree().ReloadCurrentScene();   // Session.Next still holds this encounter; a loss restored HP
		var work = BigButton("GO TO WORK   Z", primary: true, GoToSelect);
		At(screen, Ui.H(16, work, BigButton("RETRY THE BATTLE   R", primary: false, _onRetry)), 160, 480);
		work.GrabFocus();
	}

	static Button BigButton(string text, bool primary, Action onPressed)
	{
		var b = new Button { Text = text, CustomMinimumSize = new Vector2(0, 64) };
		var normal = primary ? Ui.Box(Ui.Amber, radius: 10, padX: 28) : Ui.Box(Colors.Transparent, Ui.Muted, 2, 10, 28);
		foreach (var s in new[] { "normal", "hover", "pressed" }) b.AddThemeStyleboxOverride(s, normal);
		var focus = Ui.Box(Colors.Transparent, Ui.Text, 2, 10);
		focus.DrawCenter = false;
		b.AddThemeStyleboxOverride("focus", focus);
		foreach (var c in new[] { "font_color", "font_hover_color", "font_focus_color", "font_pressed_color" })
			b.AddThemeColorOverride(c, primary ? Ui.Ground : Ui.Text);
		b.AddThemeFontOverride("font", Ui.CondBold);
		b.AddThemeFontSizeOverride("font_size", 26);
		b.Pressed += onPressed;
		return b;
	}
}
