using System.Windows;
using System.Windows.Controls;

namespace LEPTA.Controllers.Views;

internal sealed class ModelsControllerViews
{
    public required ModelsSelectionViews Selection { get; init; }

    public required ModelsConfigurationViews Configuration { get; init; }

    public required ModelsDeploymentViews Deployment { get; init; }
}

internal sealed class ModelsSelectionViews
{
    public required ListBox ModelsList { get; init; }

    public required ComboBox ChatServerCombo { get; init; }

    public required TextBlock ModelNoteText { get; init; }
}

internal sealed class ModelsConfigurationViews
{
    public required TextBlock ConfigurationTitleText { get; init; }

    public required TextBox NameBox { get; init; }

    public required ComboBox DeploymentModeBox { get; init; }

    public required FrameworkElement HttpServerRow { get; init; }

    public required FrameworkElement ApiKeyRow { get; init; }

    public required FrameworkElement ServedModelsRow { get; init; }

    public required ComboBox ServedModelsCombo { get; init; }

    public required TextBlock ServedModelsHintText { get; init; }

    public required TextBox HttpServerAddressBox { get; init; }

    public required TextBlock ModelFieldLabelText { get; init; }

    public required TextBox ModelBox { get; init; }

    public required FrameworkElement LocalFolderRow { get; init; }

    public required TextBox LocalPathBox { get; init; }

    public required TextBlock ServedModelNameLabelText { get; init; }

    public required FrameworkElement ServedModelNameRow { get; init; }

    public required TextBox ServedModelNameBox { get; init; }

    public required TextBox DockerImageBox { get; init; }

    public required FrameworkElement LocalMetadataBorder { get; init; }

    public required TextBlock LocalModelMetadataText { get; init; }

    public required TextBox PortBox { get; init; }

    public required ComboBox DTypeBox { get; init; }

    public required TextBox GpuBox { get; init; }

    public required TextBox GpuVramBox { get; init; }

    public required TextBox MaxLenBox { get; init; }

    public required TextBox ReadyTimeoutBox { get; init; }

    public required FrameworkElement LocalRuntimeSettingsPanel { get; init; }

    public required TextBlock ParameterCountText { get; init; }

    public required ComboBox WeightQuantizationBox { get; init; }

    public required TextBox TensorParallelBox { get; init; }

    public required ComboBox KCacheQuantizationBox { get; init; }

    public required ComboBox VCacheQuantizationBox { get; init; }

    public required ComboBox TokenizersParallelismBox { get; init; }

    public required TextBox AdditionalVllmArgumentsBox { get; init; }

    public required System.Windows.Controls.PasswordBox ApiKeyBox { get; init; }

    public required TextBox ApiKeyRevealBox { get; init; }

    public required CheckBox ApiKeyRevealCheckBox { get; init; }

    public required TextBox AuthHeaderNameBox { get; init; }

    public required TextBox AuthHeaderSchemeBox { get; init; }

    public required TextBox ExtraHeadersBox { get; init; }

    public required TextBox ExtraBodyBox { get; init; }

    public required TextBlock RequestOverridesErrorText { get; init; }

    public required Button OpenRouterPresetButton { get; init; }

    public required TextBox CpuOffloadBox { get; init; }

    public required TextBox MaxNumSeqsBox { get; init; }

    public required CheckBox VerboseLogsCheckBox { get; init; }
}

internal sealed class ModelsDeploymentViews
{
    public required Border DockerStatusIndicator { get; init; }

    public required TextBlock DockerStatusText { get; init; }

    public required TextBlock DockerStatusDetailsText { get; init; }

    public required TextBlock EstimatedVramText { get; init; }

    public required TextBlock EstimateSummaryText { get; init; }

    public required Button CheckServerButton { get; init; }

    public required Button OpenAdvancedConfigurationButton { get; init; }

    public required FrameworkElement EstimateBorder { get; init; }

    public required FrameworkElement DockerStatusBorder { get; init; }

    public required FrameworkElement DeploymentLogBorder { get; init; }

    public required FrameworkElement ModelActionsBorder { get; init; }

    public required Button StartServerButton { get; init; }

    public required Button StopServerButton { get; init; }

    public required Button RestartServerButton { get; init; }

    public required TextBox DeploymentLogBox { get; init; }

    public required ProgressBar ModelProgress { get; init; }

    public required ProgressBar ChatProgress { get; init; }

    public required FrameworkElement AdvancedConfigurationPanel { get; init; }
}
