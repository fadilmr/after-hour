using System.Collections.Generic;
using AfterHour.Combat;
using Godot;

namespace AfterHour;

/// <summary>
/// The battle UI's look, from the "After Hours Battle UI" canvas: colors, the three typefaces, the shared Theme,
/// and small builders for labels, boxes and bars. Amber means "act on this now" and nothing else.
/// </summary>
public static class Ui
{
	public static readonly Color Ground = new("#0B1020"), Panel = new("#121A2E"), Row = new("#0D1426"), Raised = new("#1A2340"),
		Line = new("#28324F"), Muted = new("#3A4568"), Text = new("#E8ECF5"), Dim = new("#9AA6C2"), Amber = new("#FFB547"),
		Danger = new("#FF6B5E"), HpFill = new("#DDE3EE"), Mint = new("#7FE0C8"), Sky = new("#7FC8FF"), Shape = new("#161F38"),
		Faint = new(1, 1, 1, 0.4f);

	public static Color Member(string id) => id switch
	{
		"noa" => new("#7FE0C8"),
		"rook" => new("#B88CFF"),
		"ines" => new("#FF7A45"),
		"bram" => new("#6FB6FF"),   // not on the boards
		_ => Text,
	};

	public static Color Of(Element e) => e switch
	{
		Element.Heat => new("#FF7A45"),
		Element.Volt => new("#F5E04A"),
		Element.Frost => new("#7FC8FF"),
		Element.Resonance => new("#B88CFF"),
		_ => HpFill,
	};

	static Font? _body, _semi, _cond, _condBold, _mono, _monoBold;
	public static Font Body => _body ??= GD.Load<FontFile>("res://ui/fonts/Barlow-Regular.ttf");
	public static Font Semi => _semi ??= GD.Load<FontFile>("res://ui/fonts/Barlow-SemiBold.ttf");
	public static Font Cond => _cond ??= GD.Load<FontFile>("res://ui/fonts/BarlowCondensed-SemiBold.ttf");
	public static Font CondBold => _condBold ??= GD.Load<FontFile>("res://ui/fonts/BarlowCondensed-Bold.ttf");
	public static Font Mono => _mono ??= MonoWeight(500);
	public static Font MonoBold => _monoBold ??= MonoWeight(700);

	static Font MonoWeight(int weight) => new FontVariation
	{
		BaseFont = GD.Load<FontFile>("res://ui/fonts/JetBrainsMono.ttf"),
		VariationOpentype = new Godot.Collections.Dictionary { { TextServerManager.GetPrimaryInterface().NameToTag("wght"), weight } },
	};

	static Theme? _theme;
	public static Theme Theme => _theme ??= BuildTheme();

	static Theme BuildTheme()
	{
		var t = new Theme { DefaultFont = Body, DefaultFontSize = 20 };
		t.SetStylebox("normal", "Button", Box(Row, radius: 8, padX: 16, padY: 8));
		t.SetStylebox("hover", "Button", Box(Raised, radius: 8, padX: 16, padY: 8));
		t.SetStylebox("pressed", "Button", Box(Raised, radius: 8, padX: 16, padY: 8));
		t.SetStylebox("disabled", "Button", Box(Row with { A = 0.5f }, radius: 8, padX: 16, padY: 8));
		var focus = Box(Colors.Transparent, Amber, 2, 8);
		focus.DrawCenter = false;
		t.SetStylebox("focus", "Button", focus);
		foreach (var c in new[] { "font_color", "font_hover_color", "font_focus_color", "font_pressed_color", "font_hover_pressed_color" })
			t.SetColor(c, "Button", Text);
		t.SetColor("font_disabled_color", "Button", Dim);
		t.SetFont("font", "Button", Cond);
		t.SetFontSize("font_size", "Button", 20);
		t.SetStylebox("panel", "PanelContainer", Box(Panel, Line, 1, 12, 16, 16));
		t.SetColor("font_color", "Label", Text);
		t.SetStylebox("normal", "RichTextLabel", new StyleBoxEmpty());
		t.SetFont("normal_font", "RichTextLabel", Mono);
		t.SetFontSize("normal_font_size", "RichTextLabel", 16);
		t.SetColor("default_color", "RichTextLabel", Text);
		return t;
	}

	public static StyleBoxFlat Box(Color bg, Color? border = null, int borderWidth = 0, int radius = 8, int padX = 0, int padY = 0)
	{
		var s = new StyleBoxFlat { BgColor = bg, BorderColor = border ?? Colors.Transparent };
		s.SetBorderWidthAll(border == null ? 0 : borderWidth);
		s.SetCornerRadiusAll(radius);
		s.ContentMarginLeft = s.ContentMarginRight = padX;
		s.ContentMarginTop = s.ContentMarginBottom = padY;
		return s;
	}

	public static Label L(string text, Font font, int size, Color? color = null)
	{
		var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center };
		label.AddThemeFontOverride("font", font);
		label.AddThemeFontSizeOverride("font_size", size);
		label.AddThemeColorOverride("font_color", color ?? Text);
		return label;
	}

	public static PanelContainer Wrap(Control child, StyleBoxFlat style)
	{
		var p = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
		p.AddThemeStyleboxOverride("panel", style);
		p.AddChild(child);
		return p;
	}

	/// <summary>A small rounded tag: "HEAT", "BURN 2", "NOW".</summary>
	public static PanelContainer Chip(string text, Color color, Color? fill = null, int size = 15) =>
		Wrap(L(text, CondBold, size, fill == null ? color : Ground), Box(fill ?? Colors.Transparent, fill == null ? color : null, 2, 6, 8, 1));

	/// <summary>A flat bar; height in px, frac in 0..1.</summary>
	public static ProgressBar Bar(double frac, Color fill, int height)
	{
		var bar = new ProgressBar
		{
			MaxValue = 1, Step = 0, Value = frac, ShowPercentage = false, CustomMinimumSize = new Vector2(0, height),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		bar.AddThemeStyleboxOverride("fill", Box(fill, radius: height / 2));
		bar.AddThemeStyleboxOverride("background", Box(Line, radius: height / 2));
		return bar;
	}

	public static HBoxContainer H(int gap, params Control[] children) => Fill(new HBoxContainer(), gap, children);
	public static VBoxContainer V(int gap, params Control[] children) => Fill(new VBoxContainer(), gap, children);

	static T Fill<T>(T box, int gap, IEnumerable<Control> children) where T : BoxContainer
	{
		box.MouseFilter = Control.MouseFilterEnum.Ignore;
		box.AddThemeConstantOverride("separation", gap);
		foreach (var c in children) box.AddChild(c);
		return box;
	}

	public static Control Spacer() => new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };

	/// <summary>Timeline units as m:ss; one unit is one second, so a 100-SPD turn reads 1:40.</summary>
	public static string Clock(double t)
	{
		var s = (int)System.Math.Round(t);
		return $"{s / 60}:{s % 60:00}";
	}
}
