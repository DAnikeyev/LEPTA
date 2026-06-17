using LEPTA.Models;
using LEPTA.Shared.Diagnostics;
using LEPTA.Shared.Models;
using LEPTA.vLLM.Models;

namespace LEPTA.Controllers;

internal sealed partial class LeptaController
{
    private const int MaxMermaidRepairAttempts = 3;
    private static readonly TimeSpan MermaidRepairValidationTimeout = TimeSpan.FromSeconds(20);
    private readonly Dictionary<Guid, MermaidRepairTracker> mermaidRepairTrackers = [];

    private sealed class MermaidRepairTracker
    {
        public int AttemptsStarted { get; set; }

        public bool IsLoopRunning { get; set; }

        public string? LastBrokenText { get; set; }

        public TaskCompletionSource<string?>? PendingValidation { get; set; }

        public CancellationTokenSource? Cancellation { get; set; }
    }

    private void HandleMermaidRenderStateChanged(LeptaPanelStateBase panelState)
    {
        if (!string.Equals(panelState.Format, LeptaPanelFormats.Mermaid, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!mermaidRepairTrackers.TryGetValue(panelState.Id, out var tracker))
        {
            tracker = new MermaidRepairTracker();
            mermaidRepairTrackers[panelState.Id] = tracker;
        }

        if (tracker.PendingValidation is { } pendingValidation)
        {
            pendingValidation.TrySetResult(panelState.RenderErrorMessage);
            return;
        }

        if (string.IsNullOrWhiteSpace(panelState.RenderErrorMessage)
            || tracker.IsLoopRunning
            || tracker.AttemptsStarted >= MaxMermaidRepairAttempts)
        {
            return;
        }

        tracker.Cancellation?.Cancel();
        tracker.Cancellation?.Dispose();
        tracker.Cancellation = new CancellationTokenSource();
        _ = RunMermaidRepairLoopAsync(panelState, tracker, tracker.Cancellation.Token);
    }

    private async Task RunMermaidRepairLoopAsync(
        LeptaPanelStateBase panelState,
        MermaidRepairTracker tracker,
        CancellationToken cancellationToken)
    {
        tracker.IsLoopRunning = true;
        try
        {
            while (!cancellationToken.IsCancellationRequested
                   && !string.IsNullOrWhiteSpace(panelState.RenderErrorMessage)
                   && tracker.AttemptsStarted < MaxMermaidRepairAttempts)
            {
                var attempt = tracker.AttemptsStarted + 1;
                await panelsView.ItemsControl.Dispatcher.InvokeAsync(() =>
                    panelState.SetRenderRepairStatus(true, $"Attempt {attempt} of {MaxMermaidRepairAttempts}: repairing Mermaid diagram..."));
                PublishAction($"Panel '{panelState.Name}': Mermaid auto-repair attempt {attempt}/{MaxMermaidRepairAttempts} started.");

                if (!TryResolveMermaidRepairTarget(out var server, out var model))
                {
                    tracker.AttemptsStarted = MaxMermaidRepairAttempts;
                    await panelsView.ItemsControl.Dispatcher.InvokeAsync(() =>
                        panelState.SetRenderRepairStatus(false, $"Attempt {attempt}: failed. Mermaid auto-repair needs a connected server and resolved model."));
                    PublishAction($"Panel '{panelState.Name}': Attempt {attempt} failed. Mermaid auto-repair needs a connected server and resolved model.", ActionLogLevel.Error);
                    return;
                }

                tracker.AttemptsStarted = attempt;
                tracker.LastBrokenText = panelState.Response;
                var repairResponse = await requestOrchestrator.RepairMermaidDiagramAsync(
                    server.Endpoint,
                    model,
                    panelState.Response,
                    panelState.RenderErrorMessage ?? "Unknown Mermaid render error.",
                    enableThinking: server.SupportsThinking && run.ThinkingCheckBox.IsChecked == true,
                    temperature: currentTemperature,
                    maxModelLength: server.MaxModelLength,
                    apiKey: server.ApiKey,
                    cancellationToken: cancellationToken);

                if (!string.IsNullOrWhiteSpace(repairResponse.Error) || string.IsNullOrWhiteSpace(repairResponse.Text))
                {
                    var failureMessage = $"Attempt {attempt}: failed. {repairResponse.Error ?? "The model returned no Mermaid content."}";
                    await panelsView.ItemsControl.Dispatcher.InvokeAsync(() =>
                        panelState.SetRenderRepairStatus(false, failureMessage));
                    PublishAction($"Panel '{panelState.Name}': {failureMessage}", attempt >= MaxMermaidRepairAttempts ? ActionLogLevel.Error : ActionLogLevel.Warning);
                    continue;
                }

                if (IsRepairedTextUnchanged(tracker.LastBrokenText, repairResponse.Text))
                {
                    var unchangedMessage = $"Attempt {attempt}: failed. Repair returned the same broken syntax.";
                    await panelsView.ItemsControl.Dispatcher.InvokeAsync(() =>
                        panelState.SetRenderRepairStatus(false, unchangedMessage));
                    PublishAction($"Panel '{panelState.Name}': {unchangedMessage}", attempt >= MaxMermaidRepairAttempts ? ActionLogLevel.Error : ActionLogLevel.Warning);
                    continue;
                }

                tracker.PendingValidation = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                await panelsView.ItemsControl.Dispatcher.InvokeAsync(() =>
                {
                    panelState.SetRenderRepairStatus(true, $"Attempt {attempt} submitted. Validating repaired Mermaid render...");
                    panelState.Status = string.Empty;
                    panelState.Response = repairResponse.Text;
                });
                PublishAction($"Panel '{panelState.Name}': Mermaid auto-repair attempt {attempt}/{MaxMermaidRepairAttempts} submitted for validation.");

                var completedTask = await Task.WhenAny(
                    tracker.PendingValidation.Task,
                    Task.Delay(MermaidRepairValidationTimeout, cancellationToken));
                var validationTask = tracker.PendingValidation;
                tracker.PendingValidation = null;
                if (completedTask != validationTask.Task)
                {
                    var timeoutMessage = $"Attempt {attempt}: failed. Mermaid validation timed out.";
                    await panelsView.ItemsControl.Dispatcher.InvokeAsync(() =>
                        panelState.SetRenderRepairStatus(false, timeoutMessage));
                    PublishAction($"Panel '{panelState.Name}': {timeoutMessage}", attempt >= MaxMermaidRepairAttempts ? ActionLogLevel.Error : ActionLogLevel.Warning);
                    continue;
                }

                var validationError = await validationTask.Task;
                if (string.IsNullOrWhiteSpace(validationError))
                {
                    await panelsView.ItemsControl.Dispatcher.InvokeAsync(() =>
                        panelState.SetRenderRepairStatus(false, $"Mermaid auto-repair succeeded on attempt {attempt}."));
                    PublishAction($"Panel '{panelState.Name}': Mermaid auto-repair succeeded on attempt {attempt}.");
                    return;
                }

                var renderFailureMessage = $"Attempt {attempt}: failed. {validationError}";
                await panelsView.ItemsControl.Dispatcher.InvokeAsync(() =>
                    panelState.SetRenderRepairStatus(false, renderFailureMessage));
                PublishAction($"Panel '{panelState.Name}': {renderFailureMessage}", attempt >= MaxMermaidRepairAttempts ? ActionLogLevel.Error : ActionLogLevel.Warning);
            }

            if (!cancellationToken.IsCancellationRequested
                && !string.IsNullOrWhiteSpace(panelState.RenderErrorMessage)
                && tracker.AttemptsStarted >= MaxMermaidRepairAttempts)
            {
                await panelsView.ItemsControl.Dispatcher.InvokeAsync(() =>
                    panelState.SetRenderRepairStatus(false, $"Attempt {tracker.AttemptsStarted}: failed. Mermaid auto-repair exhausted after {MaxMermaidRepairAttempts} attempts."));
                PublishAction($"Panel '{panelState.Name}': Mermaid auto-repair exhausted after {MaxMermaidRepairAttempts} attempts.", ActionLogLevel.Error);
            }
        }
        catch (OperationCanceledException)
        {
            await panelsView.ItemsControl.Dispatcher.InvokeAsync(() =>
                panelState.SetRenderRepairStatus(false, null));
        }
        finally
        {
            tracker.PendingValidation?.TrySetCanceled();
            tracker.PendingValidation = null;
            tracker.IsLoopRunning = false;
            tracker.Cancellation?.Dispose();
            tracker.Cancellation = null;
        }
    }

    private bool TryResolveMermaidRepairTarget(
        out VllmServerConfiguration server,
        out string model)
    {
        server = run.ServerCombo.SelectedItem as VllmServerConfiguration ?? new VllmServerConfiguration();
        model = lastResolvedModelName ?? string.Empty;

        if (run.ServerCombo.SelectedItem is not VllmServerConfiguration selectedServer || !selectedServer.HasEstablishedConnection)
        {
            return false;
        }

        server = selectedServer;
        model = string.IsNullOrWhiteSpace(lastResolvedModelName)
            ? selectedServer.EffectiveServedModelName
            : lastResolvedModelName;
        return !string.IsNullOrWhiteSpace(model);
    }

    private static bool IsRepairedTextUnchanged(string? brokenText, string? repairedText)
    {
        if (string.IsNullOrWhiteSpace(brokenText) || string.IsNullOrWhiteSpace(repairedText))
        {
            return false;
        }

        var normalizedBroken = brokenText.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var normalizedRepaired = repairedText.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (normalizedBroken.Equals(normalizedRepaired, StringComparison.Ordinal))
        {
            return true;
        }

        // Consider unchanged if 95% of words are the same (allows minor punctuation changes)
        var brokenWords = normalizedBroken.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var repairedWords = normalizedRepaired.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (brokenWords.Length == 0 || repairedWords.Length == 0)
        {
            return false;
        }

        var commonWords = brokenWords.Intersect(repairedWords, StringComparer.OrdinalIgnoreCase).Count();
        var similarity = commonWords / (double)Math.Max(brokenWords.Length, repairedWords.Length);
        return similarity >= 0.95;
    }

    private void CancelMermaidRepair(Guid panelId)
    {
        if (!mermaidRepairTrackers.Remove(panelId, out var tracker))
        {
            return;
        }

        tracker.PendingValidation?.TrySetCanceled();
        tracker.Cancellation?.Cancel();
        tracker.Cancellation?.Dispose();
    }

    private void CancelAllMermaidRepairs()
    {
        foreach (var panelId in mermaidRepairTrackers.Keys.ToArray())
        {
            CancelMermaidRepair(panelId);
        }
    }
}

