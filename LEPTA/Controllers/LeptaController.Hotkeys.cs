using System.Globalization;
using System.Windows.Controls;
using System.Windows.Input;
using LEPTA.Shared.Models;
using LEPTA.Theming;

namespace LEPTA.Controllers;

internal sealed partial class LeptaController
{
    private static readonly string[] SeededHotkeyKeys =
    [
        .. Enumerable.Range(0, 10).Select(value => value.ToString(CultureInfo.InvariantCulture)),
        .. Enumerable.Range('A', 26).Select(value => ((char)value).ToString()),
        .. Enumerable.Range(1, 24).Select(value => $"F{value}"),
        "Enter",
        "Escape",
        "Space",
        "Tab",
        "Backspace",
        "Insert",
        "Delete",
        "Home",
        "End",
        "PageUp",
        "PageDown",
        "Up",
        "Down",
        "Left",
        "Right"
    ];

    private static readonly IReadOnlyDictionary<string, Key> HotkeyKeyAliases = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase)
    {
        ["0"] = Key.D0,
        ["1"] = Key.D1,
        ["2"] = Key.D2,
        ["3"] = Key.D3,
        ["4"] = Key.D4,
        ["5"] = Key.D5,
        ["6"] = Key.D6,
        ["7"] = Key.D7,
        ["8"] = Key.D8,
        ["9"] = Key.D9,
        ["Backspace"] = Key.Back,
        ["Del"] = Key.Delete,
        ["Delete"] = Key.Delete,
        ["Enter"] = Key.Return,
        ["Esc"] = Key.Escape,
        ["Escape"] = Key.Escape,
        ["Ins"] = Key.Insert,
        ["Insert"] = Key.Insert,
        ["PageDown"] = Key.Next,
        ["PageUp"] = Key.Prior,
        ["PgDn"] = Key.Next,
        ["PgUp"] = Key.Prior,
        ["Return"] = Key.Return,
        ["Space"] = Key.Space
    };

    public HotkeySettings GetHotkeySettings() => new()
    {
        Ctrl = hotkeys.CtrlCheckBox.IsChecked == true,
        Alt = hotkeys.AltCheckBox.IsChecked == true,
        Shift = hotkeys.ShiftCheckBox.IsChecked == true,
        Win = hotkeys.WinCheckBox.IsChecked == true,
        Key = GetConfiguredHotkeyKeyText()
    };

    public void ApplyHotkeySettings(HotkeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        suppressStateChanged = true;
        try
        {
            hotkeys.CtrlCheckBox.IsChecked = settings.Ctrl;
            hotkeys.AltCheckBox.IsChecked = settings.Alt;
            hotkeys.ShiftCheckBox.IsChecked = settings.Shift;
            hotkeys.WinCheckBox.IsChecked = settings.Win;
            var keyText = NormalizeHotkeyKeyText(settings.Key);
            hotkeys.KeyCombo.SelectedItem = hotkeys.KeyCombo.Items.Cast<object>()
                .OfType<string>()
                .FirstOrDefault(item => string.Equals(item, keyText, StringComparison.OrdinalIgnoreCase));
            hotkeys.KeyCombo.Text = keyText;
            hotkeys.PreviewText.Text = $"Current shortcut: {BuildHotkeyDisplayText()}";
        }
        finally
        {
            suppressStateChanged = false;
        }
    }

    public void HandleHotkeySettingChanged()
    {
        hotkeys.PreviewText.Text = $"Current shortcut: {BuildHotkeyDisplayText()}";
        HotkeySettingsChanged?.Invoke();
        logger.Log(nameof(LeptaController), $"Hotkey setting changed to '{BuildHotkeyDisplayText()}'.");
        OnStateChanged();
    }

    public bool TryGetHotkey(out uint modifiers, out uint virtualKey, out string displayText)
    {
        modifiers = 0;
        virtualKey = 0;
        var keyText = GetConfiguredHotkeyKeyText();
        displayText = BuildHotkeyDisplayText(keyText);

        if (hotkeys.CtrlCheckBox.IsChecked == true)
        {
            modifiers |= 0x0002;
        }

        if (hotkeys.AltCheckBox.IsChecked == true)
        {
            modifiers |= 0x0001;
        }

        if (hotkeys.ShiftCheckBox.IsChecked == true)
        {
            modifiers |= 0x0004;
        }

        if (hotkeys.WinCheckBox.IsChecked == true)
        {
            modifiers |= 0x0008;
        }

        if (!TryParseHotkeyKey(keyText, out var key))
        {
            return false;
        }

        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        return virtualKey != 0;
    }

    public void SetHotkeyRegistrationStatus(string message, bool isError = false)
    {
        hotkeys.RegistrationStatusText.Text = message;
        hotkeys.RegistrationStatusText.SetResourceReference(
            TextBlock.ForegroundProperty,
            isError ? ThemeResourceKeys.ErrorBrush : ThemeResourceKeys.SecondaryTextBrush);
    }

    private void SeedHotkeyKeys()
    {
        hotkeys.KeyCombo.ItemsSource = SeededHotkeyKeys;
    }

    private void HandleTemperatureTextChanged()
    {
        if (!TryParseTemperature(run.TemperatureTextBox.Text, out var temperature))
        {
            return;
        }

        var normalizedTemperature = LeptaSettings.NormalizeTemperature(temperature);
        if (Math.Abs(currentTemperature - normalizedTemperature) < 0.001)
        {
            return;
        }

        currentTemperature = normalizedTemperature;
        OnStateChanged();
    }

    private static string FormatTemperature(double temperature)
        => LeptaSettings.NormalizeTemperature(temperature).ToString("0.##", CultureInfo.InvariantCulture);

    private static bool TryParseTemperature(string? value, out double temperature)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out temperature))
        {
            return true;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out temperature);
    }

    private string BuildHotkeyDisplayText(string? keyText = null)
    {
        var parts = new List<string>();
        if (hotkeys.CtrlCheckBox.IsChecked == true)
        {
            parts.Add("Ctrl");
        }

        if (hotkeys.AltCheckBox.IsChecked == true)
        {
            parts.Add("Alt");
        }

        if (hotkeys.ShiftCheckBox.IsChecked == true)
        {
            parts.Add("Shift");
        }

        if (hotkeys.WinCheckBox.IsChecked == true)
        {
            parts.Add("Win");
        }

        parts.Add(string.IsNullOrWhiteSpace(keyText) ? "(key)" : NormalizeHotkeyKeyText(keyText));
        return string.Join("+", parts);
    }

    private string GetConfiguredHotkeyKeyText()
    {
        var text = hotkeys.KeyCombo.Text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            return NormalizeHotkeyKeyText(text);
        }

        return NormalizeHotkeyKeyText(hotkeys.KeyCombo.SelectedItem as string);
    }

    private static string NormalizeHotkeyKeyText(string? keyText)
    {
        var trimmed = keyText?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (HotkeyKeyAliases.TryGetValue(trimmed, out var aliasedKey))
        {
            return GetHotkeyDisplayKeyText(aliasedKey);
        }

        return Enum.TryParse<Key>(trimmed, ignoreCase: true, out var parsedKey)
            ? GetHotkeyDisplayKeyText(parsedKey)
            : trimmed;
    }

    private static bool TryParseHotkeyKey(string? keyText, out Key key)
    {
        var trimmed = keyText?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            key = Key.None;
            return false;
        }

        if (HotkeyKeyAliases.TryGetValue(trimmed, out key))
        {
            return !IsModifierOnlyKey(key);
        }

        if (Enum.TryParse<Key>(trimmed, ignoreCase: true, out key))
        {
            return !IsModifierOnlyKey(key);
        }

        key = Key.None;
        return false;
    }

    private static string GetHotkeyDisplayKeyText(Key key)
        => key switch
        {
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Escape => "Escape",
            Key.Next => "PageDown",
            Key.Prior => "PageUp",
            Key.Return => "Enter",
            Key.Space => "Space",
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture),
            _ => key.ToString()
        };

    private static bool IsModifierOnlyKey(Key key)
        => key is Key.None
            or Key.LeftAlt
            or Key.RightAlt
            or Key.LeftCtrl
            or Key.RightCtrl
            or Key.LeftShift
            or Key.RightShift
            or Key.LWin
            or Key.RWin;
}

