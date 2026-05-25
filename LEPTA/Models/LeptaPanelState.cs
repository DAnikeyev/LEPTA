using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using LEPTA.Shared.Models;

namespace LEPTA.Models;

public interface ILeptaPanelState : INotifyPropertyChanged
{
    Guid Id { get; }
    string Name { get; set; }
    string CustomInstruction { get; set; }
    string Response { get; set; }
    string Status { get; set; }
    bool IsStreaming { get; set; }
    string AccentColorHex { get; set; }
    string Format { get; }
    Brush AccentBackgroundBrush { get; }
    Brush AccentBorderBrush { get; }
    string StatusBadgeState { get; }
    string StatusBadgeGlyph { get; }
    string? StatusBadgeToolTip { get; }
}

public abstract class LeptaPanelStateBase : ILeptaPanelState
{
    public const string StatusBadgeStateNone = "None";
    public const string StatusBadgeStateRunning = "Running";
    public const string StatusBadgeStateSuccess = "Success";
    public const string StatusBadgeStateError = "Error";

    private string name = "Panel";
    private string customInstruction = string.Empty;
    private string response = string.Empty;
    private string status = string.Empty;
    private string accentColorHex = LeptaPanelAccentPalette.DefaultAccentColorHex;
    private bool isStreaming;
    private string? generationErrorMessage;
    private string? renderErrorMessage;
    private string? repairStatusMessage;
    private bool isRepairInProgress;
    private TimeSpan? generationDuration;
    private int estimatedGeneratedTokenCount;
    private bool hasGenerationResult;

    protected LeptaPanelStateBase()
        : this(Guid.NewGuid())
    {
    }

    protected LeptaPanelStateBase(Guid id)
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id;
    }

    public Guid Id { get; }

    public string Name
    {
        get => name;
        set => SetField(ref name, value);
    }

    public string CustomInstruction
    {
        get => customInstruction;
        set => SetField(ref customInstruction, value);
    }

    public string Response
    {
        get => response;
        set => SetField(ref response, value);
    }

    public string Status
    {
        get => status;
        set
        {
            if (!SetField(ref status, value))
            {
                return;
            }

            NotifyStatusBadgeChanged();
        }
    }

    public bool IsStreaming
    {
        get => isStreaming;
        set
        {
            if (!SetField(ref isStreaming, value))
            {
                return;
            }

            NotifyStatusBadgeChanged();
        }
    }

    public string AccentColorHex
    {
        get => accentColorHex;
        set
        {
            var normalized = NormalizeAccentColor(value);
            if (string.Equals(accentColorHex, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            accentColorHex = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccentColorHex)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccentBackgroundBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccentBorderBrush)));
        }
    }

    public Brush AccentBackgroundBrush => CreateBrush(alpha: 40);

    public Brush AccentBorderBrush => CreateBrush(alpha: 160);

    public string StatusBadgeState
        => ResolveStatusBadgeState();

    public string StatusBadgeGlyph
        => StatusBadgeState switch
        {
            StatusBadgeStateError => "✕",
            StatusBadgeStateSuccess => "✓",
            StatusBadgeStateRunning => "…",
            _ => string.Empty
        };

    public string? StatusBadgeToolTip => BuildStatusBadgeToolTip();

    public string? RenderErrorMessage => renderErrorMessage;

    public string? RepairStatusMessage => repairStatusMessage;

    public bool IsRepairInProgress => isRepairInProgress;

    public abstract string Format { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ResetRunState()
    {
        generationErrorMessage = null;
        renderErrorMessage = null;
        repairStatusMessage = null;
        isRepairInProgress = false;
        generationDuration = null;
        estimatedGeneratedTokenCount = 0;
        hasGenerationResult = false;
        status = string.Empty;
        NotifyPropertyChanged(nameof(Status));
        NotifyPropertyChanged(nameof(RenderErrorMessage));
        NotifyPropertyChanged(nameof(RepairStatusMessage));
        NotifyPropertyChanged(nameof(IsRepairInProgress));
        NotifyStatusBadgeChanged();
    }

    public void ApplyGenerationOutcome(int estimatedTokenCount, TimeSpan? elapsed, string? errorMessage = null)
    {
        estimatedGeneratedTokenCount = Math.Max(0, estimatedTokenCount);
        generationDuration = elapsed;
        generationErrorMessage = string.IsNullOrWhiteSpace(errorMessage)
            ? null
            : errorMessage.Trim();
        hasGenerationResult = true;
        status = generationErrorMessage ?? string.Empty;
        NotifyPropertyChanged(nameof(Status));
        NotifyStatusBadgeChanged();
    }

    public void SetRenderError(string? errorMessage)
    {
        var normalized = string.IsNullOrWhiteSpace(errorMessage)
            ? null
            : errorMessage.Trim();
        var changed = !string.Equals(renderErrorMessage, normalized, StringComparison.Ordinal);
        renderErrorMessage = normalized;
        NotifyPropertyChanged(nameof(RenderErrorMessage));
        if (changed)
        {
            NotifyStatusBadgeChanged();
        }
    }

    public void SetRenderRepairStatus(bool isInProgress, string? message)
    {
        var normalizedMessage = string.IsNullOrWhiteSpace(message)
            ? null
            : message.Trim();
        var stateChanged = isRepairInProgress != isInProgress;
        var messageChanged = !string.Equals(repairStatusMessage, normalizedMessage, StringComparison.Ordinal);
        isRepairInProgress = isInProgress;
        repairStatusMessage = normalizedMessage;
        if (stateChanged)
        {
            NotifyPropertyChanged(nameof(IsRepairInProgress));
        }

        if (messageChanged)
        {
            NotifyPropertyChanged(nameof(RepairStatusMessage));
        }

        if (stateChanged || messageChanged)
        {
            NotifyStatusBadgeChanged();
        }
    }

    internal void CopyRuntimeStateFrom(LeptaPanelStateBase source)
    {
        generationErrorMessage = source.generationErrorMessage;
        renderErrorMessage = source.renderErrorMessage;
        repairStatusMessage = source.repairStatusMessage;
        isRepairInProgress = source.isRepairInProgress;
        generationDuration = source.generationDuration;
        estimatedGeneratedTokenCount = source.estimatedGeneratedTokenCount;
        hasGenerationResult = source.hasGenerationResult;
        NotifyPropertyChanged(nameof(RenderErrorMessage));
        NotifyPropertyChanged(nameof(RepairStatusMessage));
        NotifyPropertyChanged(nameof(IsRepairInProgress));
        NotifyStatusBadgeChanged();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        NotifyPropertyChanged(propertyName);
        return true;
    }

    private Brush CreateBrush(byte alpha)
    {
        var color = (Color)ColorConverter.ConvertFromString(NormalizeAccentColor(accentColorHex))!;
        return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    private string ResolveStatusBadgeState()
    {
        if (isRepairInProgress)
        {
            return StatusBadgeStateRunning;
        }

        if (!string.IsNullOrWhiteSpace(renderErrorMessage) || !string.IsNullOrWhiteSpace(generationErrorMessage))
        {
            return StatusBadgeStateError;
        }

        if (IsStreaming)
        {
            return StatusBadgeStateRunning;
        }

        if (hasGenerationResult)
        {
            return StatusBadgeStateSuccess;
        }

        return StatusBadgeStateNone;
    }

    private string? BuildStatusBadgeToolTip()
    {
        var badgeState = ResolveStatusBadgeState();
        return badgeState switch
        {
            StatusBadgeStateError => BuildErrorToolTip(),
            StatusBadgeStateSuccess => BuildSuccessToolTip(),
            StatusBadgeStateRunning => isRepairInProgress
                ? repairStatusMessage ?? "Repairing Mermaid diagram..."
                : "Generating response...",
            _ => null
        };
    }

    private string BuildErrorToolTip()
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(repairStatusMessage))
        {
            details.Add(repairStatusMessage);
        }

        if (!string.IsNullOrWhiteSpace(renderErrorMessage))
        {
            details.Add($"Render failed: {renderErrorMessage}");
        }

        if (!string.IsNullOrWhiteSpace(generationErrorMessage))
        {
            details.Add($"Generation failed: {generationErrorMessage}");
        }

        AppendStatistics(details);
        return string.Join(Environment.NewLine, details);
    }

    private string BuildSuccessToolTip()
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(repairStatusMessage))
        {
            details.Add(repairStatusMessage);
        }

        AppendStatistics(details);
        return details.Count == 0
            ? "Completed successfully."
            : string.Join(Environment.NewLine, details);
    }

    private void AppendStatistics(List<string> details)
    {
        if (generationDuration is { } elapsed)
        {
            details.Add($"Generation time: {elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s");
            if (estimatedGeneratedTokenCount > 0 && elapsed.TotalSeconds > 0.01)
            {
                var tokensPerSecond = estimatedGeneratedTokenCount / elapsed.TotalSeconds;
                details.Add($"Throughput: {tokensPerSecond.ToString("0", CultureInfo.InvariantCulture)} tok/s");
            }
        }

        if (estimatedGeneratedTokenCount > 0)
        {
            details.Add($"Estimated output tokens: {estimatedGeneratedTokenCount.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private void NotifyStatusBadgeChanged()
    {
        NotifyPropertyChanged(nameof(StatusBadgeState));
        NotifyPropertyChanged(nameof(StatusBadgeGlyph));
        NotifyPropertyChanged(nameof(StatusBadgeToolTip));
    }

    private void NotifyPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string NormalizeAccentColor(string? value)
        => LeptaPanelAccentPalette.Normalize(value);
}

public sealed class MarkdownLeptaPanelState : LeptaPanelStateBase
{
    public MarkdownLeptaPanelState()
    {
    }

    public MarkdownLeptaPanelState(Guid id)
        : base(id)
    {
    }

    public override string Format => LeptaPanelFormats.Markdown;
}

public sealed class MermaidLeptaPanelState : LeptaPanelStateBase
{
    public MermaidLeptaPanelState()
    {
    }

    public MermaidLeptaPanelState(Guid id)
        : base(id)
    {
    }

    public override string Format => LeptaPanelFormats.Mermaid;
}

public sealed class PlainTextLeptaPanelState : LeptaPanelStateBase
{
    public PlainTextLeptaPanelState()
    {
    }

    public PlainTextLeptaPanelState(Guid id)
        : base(id)
    {
    }

    public override string Format => LeptaPanelFormats.PlainText;
}

public static class LeptaPanelStateFactory
{
    public static ILeptaPanelState Create(
        string? format,
        string name,
        string customInstruction,
        string? accentColorHex,
        Guid? id = null)
    {
        var panel = CreateEmpty(format, id);
        panel.Name = name;
        panel.CustomInstruction = customInstruction;
        panel.AccentColorHex = LeptaPanelAccentPalette.Normalize(accentColorHex);
        return panel;
    }

    public static ILeptaPanelState Convert(ILeptaPanelState source, string? format)
    {
        ArgumentNullException.ThrowIfNull(source);

        var panel = CreateEmpty(format, source.Id);
        panel.Name = source.Name;
        panel.CustomInstruction = source.CustomInstruction;
        panel.AccentColorHex = source.AccentColorHex;
        panel.Response = source.Response;
        panel.Status = source.Status;
        panel.IsStreaming = source.IsStreaming;
        if (source is LeptaPanelStateBase sourceState && panel is LeptaPanelStateBase targetState)
        {
            targetState.CopyRuntimeStateFrom(sourceState);
        }

        return panel;
    }

    private static ILeptaPanelState CreateEmpty(string? format, Guid? id)
        => LeptaPanelFormats.Normalize(format) switch
        {
            LeptaPanelFormats.Mermaid => new MermaidLeptaPanelState(id ?? Guid.NewGuid()),
            LeptaPanelFormats.PlainText => new PlainTextLeptaPanelState(id ?? Guid.NewGuid()),
            _ => new MarkdownLeptaPanelState(id ?? Guid.NewGuid())
        };
}

