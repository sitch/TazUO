#nullable enable
using System;
using System.Collections.Generic;
using ClassicUO.Configuration;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Game.UI.MyraWindows.Widgets;
using ClassicUO.Utility;
using Microsoft.Xna.Framework;
using Myra.Events;
using Myra.Graphics2D;
using Myra.Graphics2D.Brushes;
using Myra.Graphics2D.UI;
using Myra.Graphics2D.UI.Styles;
using Label = Myra.Graphics2D.UI.Label;

namespace ClassicUO.Game.UI.MyraWindows;

public class OptionsWindow : MyraControl
{
    /// <summary>
    /// Category, (sub category?, widget)
    /// </summary>
    private readonly Dictionary<string, List<OptionItem>> _options = new();

    private readonly MyraGrid _mainArea = new();
    private readonly VerticalStackPanel _optionsPanel = new();
    private readonly VerticalStackPanel _searchPanel = new();
    private readonly VerticalStackPanel _optionsStack = new();
    private readonly MyraInputBox _searchField = new();
    private string _lastCategory = string.Empty;

    public OptionsWindow() : base("Options")
    {
        UIManager.ForEach<OptionsWindow>(w =>
        {
            if (w != this) w.Dispose();
        });

        SetupOptions();
        Build();

        CenterInViewPort();
    }

    private void SetupOptions()
    {
        SetupGeneralOptions();
        SetupMobileOptions();
        SetupInterfaceOptions();
        SetupMiscOptions();
    }

    private void Build()
    {
        _mainArea.MinWidth = 400;
        _mainArea.MinHeight = 400;

        _mainArea.AddColumn(Proportion.Auto);
        _mainArea.AddColumn(Proportion.Fill);
        _mainArea.AddRow(Proportion.Auto);
        _mainArea.AddRow(Proportion.Fill);

        _searchField.HintText = "Search...";
        _searchField.TextChangedByUser += SearchFieldOnTextChangedByUser;
        _mainArea.AddWidget(_searchField, 0, 0, null, 2);

        VerticalStackPanel categoryPanel = new() { Spacing = MyraStyle.STANDARD_SPACING };
        _mainArea.AddWidget(categoryPanel.WrapInScroll(800), 1, 0);

        _optionsStack.Widgets.Add(_optionsPanel);
        _mainArea.AddWidget(_optionsStack, 1, 1);

        foreach (string category in _options.Keys) categoryPanel.Widgets.Add(GetCategoryButton(category));

        MyraButton GetCategoryButton(string category)
        {
            return ApplyTabStyleToButton(new MyraButton(category, () => { ShowPage(category); }));
        }

        SetRootContent(_mainArea);
    }

    private void SearchFieldOnTextChangedByUser(object? sender, ValueChangedEventArgs<string> e)
    {
        string search = e.NewValue?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(search))
        {
            _optionsStack.Widgets.Clear();
            _optionsStack.Widgets.Add(_optionsPanel);

            if (_lastCategory.NotNullNotEmpty())
                ShowPage(_lastCategory);
        }
        else
        {
            _searchPanel.Widgets.Clear();
            foreach (var (_, items) in _options)
            {
                foreach (OptionItem item in items)
                {
                    if (item.MatchesSearch(search))
                        _searchPanel.Widgets.Add(item.GetWidget);
                }
            }

            _optionsStack.Widgets.Clear();
            _optionsStack.Widgets.Add(_searchPanel);
        }
    }

    private static ButtonStyle _tabButtonStyle = null!;

    private MyraButton ApplyTabStyleToButton(MyraButton tabButton)
    {
        if (_tabButtonStyle == null!)
        {
            ButtonStyle tabControlStyle = Stylesheet.Current.ButtonStyle;
            _tabButtonStyle = new ButtonStyle(tabControlStyle)
            {
                Background = new SolidBrush(Color.Transparent),
                Border = new SolidBrush(new Color(0, 0, 0, MyraStyle.STANDARD_BORDER_ALPHA)),
                BorderThickness = new Thickness(1),
                LabelStyle = { Font = MyraStyle.UiFont },
                OverBackground = new SolidBrush(new Color(0, 0, 0, 55)),
                PressedBackground = new SolidBrush(new Color(0, 0, 0, 155)),
                MinWidth = 150
            };
        }

        tabButton.ApplyButtonStyle(_tabButtonStyle);

        return tabButton;
    }

    private void ShowPage(string category)
    {
        _searchField.Text = string.Empty;
        _optionsStack.Widgets.Clear();
        _optionsStack.Widgets.Add(_optionsPanel);

        _optionsPanel.Widgets.Clear();

        foreach (OptionItem optionItem in _options[category]) _optionsPanel.Widgets.Add(optionItem.GetWidget);

        _lastCategory = category;
    }

    private static OptionItem CreateCheckboxOption(string label, bool enabled, Action<bool> onChange,
        string? tooltip = null) =>
        new(label, () => MyraCheckButton.CreateWithCallback(enabled, onChange, label, tooltip));

    private static OptionItem CreateSliderOption(string label, float min, float max, float value,
        Action<float> onChange) =>
        new(label, () => MyraHSlider.SliderWithLabel(label, out _, onChange, min, max, value));

    private static OptionItem CreateComboBox(string label, int value, string[] options, Action<int> onChange,
        string? tooltip = null)
    {
        var comboView = new ComboView
        {
            MinWidth = 200,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        if (tooltip != null) comboView.Tooltip = tooltip;

        for (int i = 0; i < options.Length; i++)
        {
            string option = options[i];
            comboView.ListView.Widgets.Add(new Label { Text = option, Tag = i });
        }

        comboView.ListView.SelectedIndex = value;

        comboView.ListView.SelectedIndexChanged += (_, _) =>
        {
            if (comboView.ListView.SelectedIndex != null)
                onChange(comboView.ListView.SelectedIndex.Value);
        };

        return new OptionItem(label, () => new MyraLabel(label, MyraLabel.TextStyle.P).PlaceBefore(comboView));
    }

    private static OptionItem CreateHuePicker(string label, ushort hue, Action<ushort> onChange) =>
        new(label, () =>
        {
            var item = new MyraArtTexture(0x0FAB) { Tooltip = $"Current hue: {hue}" };
            item.TouchUp += (_, _) =>
            {
                UIManager.GetGump<ModernColorPicker>()?.Dispose();
                UIManager.Add(new ModernColorPicker(World.Instance, onChange));
            };

            return item;
        });

    private static OptionItem CreateInputField(string label, string text, Action<string> onChange,
        string? tooltip = null) => new(label, () =>
    {
        HorizontalStackPanel wid = MyraInputBox.WithLabel(label, out MyraInputBox inputBox, text: text, tooltip: tooltip);
        inputBox.TextChangedByUser += (_, _) => onChange(inputBox.Text);
        return wid;
    });

    private static OptionItem CreateSpacer() => new(string.Empty, () => new MyraSpacer(1, 3));

    private void SetupGeneralOptions()
    {
        Profile profile = ProfileManager.CurrentProfile;
        ModernOptionsGumpLanguage lang = Language.Instance.GetModernOptionsGumpLanguage;

        if (!_options.ContainsKey("General")) _options.Add("General", new List<OptionItem>());

        List<OptionItem> general = _options["General"];
        ModernOptionsGumpLanguage.General genLang = lang.GetGeneral;

        general.Add(CreateCheckboxOption(genLang.HighlightObjects, profile.HighlightGameObjects,
            b => profile.HighlightGameObjects = b));

        general.Add(CreateSpacer());

        general.Add(
            CreateCheckboxOption(genLang.Pathfinding, profile.EnablePathfind, b => profile.EnablePathfind = b)
                .SetTags("pathfinding, pathing, path"));
        general.Add(
            CreateCheckboxOption(genLang.ShiftPathfinding, profile.UseShiftToPathfind,
                b => profile.UseShiftToPathfind = b).SetTags("pathfinding, pathing, path"));
        general.Add(CreateCheckboxOption(genLang.SingleClickPathfind, profile.PathfindSingleClick,
            b => profile.PathfindSingleClick = b).SetTags("pathfinding, pathing, path"));

        general.Add(CreateSpacer());

        general.Add(CreateCheckboxOption(genLang.AlwaysRun, profile.AlwaysRun, b => profile.AlwaysRun = b));
        general.Add(CreateCheckboxOption(genLang.RunUnlessHidden, profile.AlwaysRunUnlessHidden,
            b => profile.AlwaysRunUnlessHidden = b));

        general.Add(CreateSpacer());

        general.Add(CreateCheckboxOption(genLang.AutoOpenDoors, profile.AutoOpenDoors,
            b => profile.AutoOpenDoors = b));
        general.Add(CreateCheckboxOption(genLang.AutoOpenPathfinding, profile.SmoothDoors,
            b => profile.SmoothDoors = b));

        general.Add(CreateSpacer());

        general.Add(CreateCheckboxOption(genLang.AutoOpenCorpse, profile.AutoOpenCorpses,
            b => profile.AutoOpenCorpses = b).SetTags("corpse, loot"));
        general.Add(CreateSliderOption(genLang.CorpseOpenDistance, 0, 5, profile.AutoOpenCorpseRange,
            f => profile.AutoOpenCorpseRange = (int)f).SetTags("corpse, loot"));
        general.Add(CreateCheckboxOption(genLang.CorpseSkipEmpty, profile.SkipEmptyCorpse,
                b => profile.SkipEmptyCorpse = b,
                "Most servers don't send corpse contents until it's opened.\nEnabling this will make this feature not work on most servers.")
            .SetTags("corpse, loot"));
        general.Add(CreateComboBox(genLang.CorpseOpenOptions, profile.CorpseOpenOptions, [
            genLang.CorpseOptNone, genLang.CorpseOptNotTarg,
            genLang.CorpseOptNotHiding, genLang.CorpseOptBoth
        ], i => profile.CorpseOpenOptions = i).SetTags("corpse, loot"));

        general.Add(CreateSpacer());

        general.Add(CreateCheckboxOption(genLang.OutRangeColor, profile.NoColorObjectsOutOfRange,
            b => profile.NoColorObjectsOutOfRange = b));
        general.Add(CreateCheckboxOption(genLang.SallosEasyGrab, profile.SallosEasyGrab,
            b => profile.SallosEasyGrab = b, genLang.SallosTooltip));
        general.Add(CreateCheckboxOption(genLang.ShowHouseContent, profile.ShowHouseContent,
            b => profile.ShowHouseContent = b, genLang.ClientVersionLimitedTooltip));
        general.Add(CreateCheckboxOption(genLang.SmoothBoat, profile.UseSmoothBoatMovement,
            b => profile.UseSmoothBoatMovement = b, genLang.ClientVersionLimitedTooltip));
        //general.Add(CreateCheckboxOption(, , b =>  = b));
    }

    private void SetupMobileOptions()
    {
        Profile profile = ProfileManager.CurrentProfile;
        ModernOptionsGumpLanguage lang = Language.Instance.GetModernOptionsGumpLanguage;

        if (!_options.ContainsKey("Mobiles")) _options.Add("Mobiles", new List<OptionItem>());
        List<OptionItem> mobiles = _options["Mobiles"];
        mobiles.Add(
            CreateCheckboxOption(lang.GetGeneral.ShowMobileHP, profile.ShowMobilesHP, b => profile.ShowMobilesHP = b)
                .SetTags("hp, health, hit point"));
        mobiles.Add(CreateComboBox(lang.GetGeneral.MobileHPType, profile.MobileHPType,
            [lang.GetGeneral.HPTypePerc, lang.GetGeneral.HPTypeBar, lang.GetGeneral.HPTypeNBoth],
            i => profile.MobileHPType = i).SetTags("hp, health, hit point"));
        mobiles.Add(CreateComboBox(lang.GetGeneral.HPShowWhen, profile.MobileHPShowWhen,
            [lang.GetGeneral.HPShowWhen_Always, lang.GetGeneral.HPShowWhen_Less100, lang.GetGeneral.HPShowWhen_Smart],
            i => profile.MobileHPShowWhen = i).SetTags("hp, health, hit point"));

        mobiles.Add(CreateSpacer());

        mobiles.Add(CreateCheckboxOption(lang.GetGeneral.HighlightPoisoned, profile.HighlightMobilesByPoisoned,
            b => profile.HighlightMobilesByPoisoned = b));
        mobiles.Add(
            CreateHuePicker(lang.GetGeneral.PoisonHighlightColor, profile.PoisonHue, h => profile.PoisonHue = h));

        mobiles.Add(CreateCheckboxOption(lang.GetGeneral.HighlightPara, profile.HighlightMobilesByParalize,
            b => profile.HighlightMobilesByParalize = b));
        mobiles.Add(CreateHuePicker(lang.GetGeneral.ParaHighlightColor, profile.ParalyzedHue,
            h => profile.ParalyzedHue = h));

        mobiles.Add(CreateCheckboxOption(lang.GetGeneral.HighlightInvul, profile.HighlightMobilesByInvul,
            b => profile.HighlightMobilesByInvul = b));
        mobiles.Add(CreateHuePicker(lang.GetGeneral.InvulHighlightColor, profile.InvulnerableHue,
            h => profile.InvulnerableHue = h));

        mobiles.Add(CreateCheckboxOption(lang.GetGeneral.IncomingMobiles, profile.ShowNewMobileNameIncoming,
            b => profile.ShowNewMobileNameIncoming = b));
        mobiles.Add(CreateCheckboxOption(lang.GetGeneral.IncomingCorpses, profile.ShowNewCorpseNameIncoming,
            b => profile.ShowNewCorpseNameIncoming = b));

        mobiles.Add(CreateComboBox(lang.GetGeneral.AuraUnderFeet, profile.AuraUnderFeetType, [
                lang.GetGeneral.AuraOptDisabled, lang.GetGeneral.AuroOptWarmode,
                lang.GetGeneral.AuraOptCtrlShift, lang.GetGeneral.AuraOptAlways
            ],
            i => profile.AuraUnderFeetType = i));

        mobiles.Add(CreateCheckboxOption(lang.GetGeneral.AuraForParty, profile.PartyAura, b => profile.PartyAura = b));
        mobiles.Add(
            CreateHuePicker(lang.GetGeneral.AuraPartyColor, profile.PartyAuraHue, h => profile.PartyAuraHue = h));
    }

    private void SetupInterfaceOptions()
    {
        Profile profile = ProfileManager.CurrentProfile;
        ModernOptionsGumpLanguage lang = Language.Instance.GetModernOptionsGumpLanguage;

        if (!_options.ContainsKey("Interface")) _options.Add("Interface", new List<OptionItem>());
        List<OptionItem> opt = _options["Interface"];

        opt.Add(CreateCheckboxOption(lang.GetGeneral.DisableTopMenu, profile.TopbarGumpIsDisabled,
            b => profile.TopbarGumpIsDisabled = b,
            "The top menu is pretty vital in TazUO, we recommend leaving this unchecked."));

        opt.Add(CreateSpacer());

        opt.Add(CreateCheckboxOption(lang.GetGeneral.AltForAnchorsGumps, profile.HoldDownKeyAltToCloseAnchored,
            b => profile.HoldDownKeyAltToCloseAnchored = b));
        opt.Add(CreateCheckboxOption(lang.GetGeneral.AltToMoveGumps, profile.HoldAltToMoveGumps,
            b => profile.HoldAltToMoveGumps = b));
        opt.Add(CreateCheckboxOption(lang.GetGeneral.CloseEntireAnchorWithRClick,
            profile.CloseAllAnchoredGumpsInGroupWithRightClick,
            b => profile.CloseAllAnchoredGumpsInGroupWithRightClick = b));

        opt.Add(CreateSpacer());

        opt.Add(CreateCheckboxOption(lang.GetGeneral.OriginalSkillsGump, profile.StandardSkillsGump,
            b => profile.StandardSkillsGump = b));
        opt.Add(CreateCheckboxOption(lang.GetGeneral.OldStatusGump, profile.UseOldStatusGump,
            b => profile.UseOldStatusGump = b));
        opt.Add(CreateCheckboxOption(lang.GetGeneral.PartyInviteGump, profile.PartyInviteGump,
            b => profile.PartyInviteGump = b));

        opt.Add(CreateSpacer());

        opt.Add(CreateCheckboxOption(lang.GetGeneral.ModernHealthBars, profile.CustomBarsToggled,
            b => profile.CustomBarsToggled = b));
        opt.Add(CreateCheckboxOption(lang.GetGeneral.ModernHPBlackBG, profile.CBBlackBGToggled,
            b => profile.CBBlackBGToggled = b));
        opt.Add(CreateCheckboxOption(lang.GetGeneral.SaveHPBars, profile.SaveHealthbars,
            b => profile.SaveHealthbars = b));
        opt.Add(CreateComboBox(lang.GetGeneral.CloseHPGumpsWhen, profile.CloseHealthBarType, [
            lang.GetGeneral.CloseHPOptDisable, lang.GetGeneral.CloseHPOptOOR,
            lang.GetGeneral.CloseHPOptDead, lang.GetGeneral.CloseHPOptBoth
        ], b => profile.CloseHealthBarType = b));

        opt.Add(CreateSpacer());

        opt.Add(CreateComboBox(lang.GetGeneral.GridLoot, profile.GridLootType, [
            lang.GetGeneral.GridLootOptDisable, lang.GetGeneral.GridLootOptOnly,
            lang.GetGeneral.GridLootOptBoth
        ], i => profile.GridLootType = i, "This is not the same as grid containers."));

        opt.Add(CreateSpacer());

        opt.Add(CreateCheckboxOption(lang.GetGeneral.ShiftContext, profile.HoldShiftForContext,
            b => profile.HoldShiftForContext = b));
        opt.Add(CreateCheckboxOption(lang.GetGeneral.ShiftSplit, profile.HoldShiftToSplitStack,
            b => profile.HoldShiftToSplitStack = b));
    }

    private void SetupMiscOptions()
    {
        Profile profile = ProfileManager.CurrentProfile;
        ModernOptionsGumpLanguage lang = Language.Instance.GetModernOptionsGumpLanguage;

        if (!_options.ContainsKey("Misc")) _options.Add("Misc", new List<OptionItem>());
        List<OptionItem> opt = _options["Misc"];

        opt.Add(CreateCheckboxOption(lang.GetGeneral.EnableCOT, profile.UseCircleOfTransparency,
            b => profile.UseCircleOfTransparency = b).SetTags("cot, circle of transparency"));
        opt.Add(CreateSliderOption(lang.GetGeneral.COTDistance, Constants.MIN_CIRCLE_OF_TRANSPARENCY_RADIUS,
            Constants.MAX_CIRCLE_OF_TRANSPARENCY_RADIUS, profile.CircleOfTransparencyRadius,
            f => profile.CircleOfTransparencyRadius = (int)f).SetTags("cot, circle of transparency"));
        opt.Add(CreateComboBox(lang.GetGeneral.COTType, profile.CircleOfTransparencyType, [
            lang.GetGeneral.COTTypeOptFull, lang.GetGeneral.COTTypeOptGrad,
            lang.GetGeneral.COTTypeOptModern
        ], i => profile.CircleOfTransparencyType = i).SetTags("cot, circle of transparency"));

        opt.Add(CreateSpacer());

        opt.Add(CreateCheckboxOption(lang.GetGeneral.HideScreenshotMessage, profile.HideScreenshotStoredInMessage,
            b => profile.HideScreenshotStoredInMessage = b));
        opt.Add(CreateCheckboxOption(lang.GetGeneral.ObjFade, profile.UseObjectsFading,
            b => profile.UseObjectsFading = b));
        opt.Add(CreateCheckboxOption(lang.GetGeneral.TextFade, profile.TextFading, b => profile.TextFading = b));
        opt.Add(CreateCheckboxOption(lang.GetGeneral.CursorRange, profile.ShowTargetRangeIndicator,
            b => profile.ShowTargetRangeIndicator = b));

        opt.Add(CreateSpacer());

        opt.Add(CreateCheckboxOption(lang.GetGeneral.DragSelectHP, profile.EnableDragSelect,
            b => profile.EnableDragSelect = b));
        opt.Add(CreateComboBox(lang.GetGeneral.DragKeyMod, profile.DragSelectModifierKey, [
            lang.GetGeneral.SharedNone, lang.GetGeneral.SharedCtrl, lang.GetGeneral.SharedShift,
            lang.GetGeneral.SharedAlt
        ], i => profile.DragSelectModifierKey = i));
        opt.Add(CreateComboBox(lang.GetGeneral.DragPlayersOnly, profile.DragSelect_PlayersModifier, [
            lang.GetGeneral.SharedNone, lang.GetGeneral.SharedCtrl, lang.GetGeneral.SharedShift,
            lang.GetGeneral.SharedAlt
        ], i => profile.DragSelect_PlayersModifier = i));
        opt.Add(CreateComboBox(lang.GetGeneral.DragMobsOnly, profile.DragSelect_MonstersModifier, [
            lang.GetGeneral.SharedNone, lang.GetGeneral.SharedCtrl, lang.GetGeneral.SharedShift,
            lang.GetGeneral.SharedAlt
        ], i => profile.DragSelect_MonstersModifier = i));
        opt.Add(CreateComboBox(lang.GetGeneral.DragNameplatesOnly, profile.DragSelect_NameplateModifier, [
            lang.GetGeneral.SharedNone, lang.GetGeneral.SharedCtrl, lang.GetGeneral.SharedShift,
            lang.GetGeneral.SharedAlt
        ], i => profile.DragSelect_NameplateModifier = i));
        opt.Add(CreateInputField(lang.GetGeneral.DragX, profile.DragSelectStartX.ToString(), s =>
        {
            if (int.TryParse(s, out int result)) profile.DragSelectStartX = result;
        }));
        opt.Add(CreateInputField(lang.GetGeneral.DragY, profile.DragSelectStartY.ToString(), s =>
        {
            if (int.TryParse(s, out int result)) profile.DragSelectStartY = result;
        }));

        opt.Add(CreateSpacer());

        opt.Add(CreateCheckboxOption(lang.GetGeneral.ShowStatsChangedMsg, profile.ShowStatsChangedMessage,
            b => profile.ShowStatsChangedMessage = b));
        opt.Add(CreateCheckboxOption(lang.GetGeneral.ShowSkillsChangedMsg, profile.ShowSkillsChangedMessage,
            b => profile.ShowSkillsChangedMessage = b));
        opt.Add(CreateSliderOption(lang.GetGeneral.ChangeVolume, 0, 100, profile.ShowSkillsChangedDeltaValue,
            f => profile.ShowSkillsChangedDeltaValue = (int)f));
    }

    private class OptionItem(string searchText, Func<Widget> createWidget, string? tags = null)
    {
        private string? _tags = tags;

        public bool MatchesSearch(string text)
        {
            if (searchText.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;

            return _tags.NotNullNotEmpty() && _tags!.Contains(text, StringComparison.OrdinalIgnoreCase);
        }

        public Widget GetWidget
        {
            get
            {
                field ??= createWidget();

                return field;
            }
        }

        public OptionItem SetTags(string tags)
        {
            _tags = tags;
            return this;
        }
    }
}
