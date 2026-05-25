using System.Windows.Controls;

namespace LEPTA.Controllers.Views;

internal sealed class LeptaControllerViews
{
    public required LeptaPanelsViews Panels { get; init; }

    public required LeptaInstructionsViews Instructions { get; init; }

    public required LeptaDashboardViews Dashboards { get; init; }

    public required LeptaPresetViews Presets { get; init; }

    public required LeptaRunViews Run { get; init; }

    public required LeptaHotkeyViews Hotkeys { get; init; }
}

internal sealed class LeptaPanelsViews
{
    public required ItemsControl ItemsControl { get; init; }
}

internal sealed class LeptaInstructionsViews
{
    public required TextBox SystemInstructionBox { get; init; }

    public required TextBox GeneralInstructionBox { get; init; }
}

internal sealed class LeptaDashboardViews
{
    public required TextBox NameBox { get; init; }

    public required ComboBox ListCombo { get; init; }
}

internal sealed class LeptaPresetViews
{
    public required TextBox NameBox { get; init; }

    public required ComboBox ListCombo { get; init; }
}

internal sealed class LeptaRunViews
{
    public required ComboBox ServerCombo { get; init; }

    public required TextBlock StatusText { get; init; }

    public required ProgressBar ProgressBar { get; init; }

    public required Button RunButton { get; init; }

    public required Button StopButton { get; init; }

    public required CheckBox ThinkingCheckBox { get; init; }

    public required TextBox TemperatureTextBox { get; init; }
}

internal sealed class LeptaHotkeyViews
{
    public required CheckBox CtrlCheckBox { get; init; }

    public required CheckBox AltCheckBox { get; init; }

    public required CheckBox ShiftCheckBox { get; init; }

    public required CheckBox WinCheckBox { get; init; }

    public required ComboBox KeyCombo { get; init; }

    public required TextBlock PreviewText { get; init; }

    public required TextBlock RegistrationStatusText { get; init; }
}
