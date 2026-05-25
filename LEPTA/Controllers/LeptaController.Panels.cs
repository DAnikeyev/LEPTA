using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using LEPTA.Models;
using LEPTA.Shared.Models;
using LEPTA.vLLM.Models;
using LEPTA.vLLM.Services;
using System.Threading.Channels;

namespace LEPTA.Controllers;

internal sealed partial class LeptaController
{
    private static readonly TimeSpan PanelResponseFlushInterval = TimeSpan.FromMilliseconds(75);

    public void AddPanel()
    {
        var nextIndex = panels.Count + 1;
        var accentColorHex = LeptaPanelAccentPalette.GetRandomAccentColor(panels.LastOrDefault()?.AccentColorHex);
        panels.Add(CreatePanelState($"Panel {nextIndex}", "Answer with the perspective for this panel.", accentColorHex, LeptaPanelFormats.Markdown));
        logger.Log(nameof(LeptaController), $"Added panel {nextIndex}. panelCount={panels.Count}.");
        OnStateChanged();
    }

    public void MovePanelLeft(Guid panelId) => MovePanel(panelId, -1);

    public void MovePanelRight(Guid panelId) => MovePanel(panelId, 1);

    public void RemovePanel(Guid panelId)
    {
        var panel = panels.FirstOrDefault(item => item.Id == panelId);
        if (panel is null)
        {
            return;
        }

        if (editingPanel?.Id == panel.Id)
        {
            editingPanel = null;
        }

        CancelMermaidRepair(panel.Id);
        DetachPanel(panel);
        panels.Remove(panel);
        if (panels.Count == 0)
        {
            panels.Add(CreatePanelState("Panel 1", "Answer with the perspective for this panel.", LeptaPanelAccentPalette.DefaultAccentColorHex, LeptaPanelFormats.Markdown));
        }

        logger.Log(nameof(LeptaController), $"Removed panel '{panel.Name}'. panelCount={panels.Count}.");
        OnStateChanged();
    }

    public bool TryOpenPanelEditor(Guid panelId, out string panelName, out string customInstruction, out string accentColorHex, out string format)
    {
        var panel = panels.FirstOrDefault(item => item.Id == panelId);
        if (panel is null)
        {
            panelName = string.Empty;
            customInstruction = string.Empty;
            accentColorHex = LeptaPanelAccentPalette.DefaultAccentColorHex;
            format = LeptaPanelFormats.Markdown;
            return false;
        }

        editingPanel = panel;
        panelName = panel.Name;
        customInstruction = panel.CustomInstruction;
        accentColorHex = panel.AccentColorHex;
        format = panel.Format;
        return true;
    }

    public void ClearPanelEditor()
        => editingPanel = null;

    public void UpdateEditingPanelName(string? name)
    {
        if (editingPanel is null)
        {
            return;
        }

        editingPanel.Name = string.IsNullOrWhiteSpace(name) ? "Panel" : name.Trim();
    }

    public void UpdateEditingPanelInstruction(string? instruction)
    {
        if (editingPanel is null)
        {
            return;
        }

        editingPanel.CustomInstruction = instruction ?? string.Empty;
    }

    public void UpdateEditingPanelAccentColor(string? accentColorHex)
    {
        if (editingPanel is null)
        {
            return;
        }

        editingPanel.AccentColorHex = LeptaPanelAccentPalette.Normalize(accentColorHex);
    }

    public void UpdateEditingPanelFormat(string? format)
    {
        if (editingPanel is null)
        {
            return;
        }

        var normalizedFormat = LeptaPanelFormats.Normalize(format);
        if (string.Equals(editingPanel.Format, normalizedFormat, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var panelIndex = panels.IndexOf(editingPanel);
        if (panelIndex < 0)
        {
            return;
        }

        var replacement = LeptaPanelStateFactory.Convert(editingPanel, normalizedFormat);
        DetachPanel(editingPanel);
        AttachPanel(replacement);
        panels[panelIndex] = replacement;
        editingPanel = replacement;
        OnStateChanged();
    }

    public void DeleteEditingPanel()
    {
        if (editingPanel is null)
        {
            return;
        }

        var panelId = editingPanel.Id;
        editingPanel = null;
        RemovePanel(panelId);
    }

    public bool TryBuildChatContinuation(
        Guid panelId,
        out string? serverId,
        out string sourceName,
        out string userPrompt,
        out string assistantResponse)
    {
        var panel = panels.FirstOrDefault(item => item.Id == panelId);
        if (panel is null || string.IsNullOrWhiteSpace(panel.Response))
        {
            serverId = null;
            sourceName = string.Empty;
            userPrompt = string.Empty;
            assistantResponse = string.Empty;
            return false;
        }

        var clipboardText = lastRunClipboardText;
        if (string.IsNullOrWhiteSpace(clipboardText))
        {
            try
            {
                clipboardText = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
            }
            catch
            {
                clipboardText = string.Empty;
            }
        }

        serverId = (run.ServerCombo.SelectedItem as VllmServerConfiguration)?.Id;
        sourceName = panel.Name;
        userPrompt = LeptaRequestOrchestrator.BuildPrompt(
            instructions.SystemInstructionBox.Text,
            clipboardText,
            instructions.GeneralInstructionBox.Text,
            panel.CustomInstruction,
            settings.DocumentTrimMode,
            settings.DocumentTokenLimit,
            panel.Format);
        assistantResponse = panel.Response;
        return true;
    }

    public void MovePanelToIndex(Guid panelId, int targetIndex)
    {
        var currentIndex = panels
            .Select((panel, index) => new { panel, index })
            .FirstOrDefault(item => item.panel.Id == panelId)
            ?.index ?? -1;
        if (currentIndex < 0)
        {
            return;
        }

        targetIndex = Math.Clamp(targetIndex, 0, panels.Count - 1);
        if (targetIndex == currentIndex)
        {
            return;
        }

        panels.Move(currentIndex, targetIndex);
        logger.Log(nameof(LeptaController), $"Moved panel from index {currentIndex} to {targetIndex}.");
        OnStateChanged();
    }

    private void ReplacePanels(IEnumerable<LeptaPanelDefinition> definitions)
    {
        CancelAllMermaidRepairs();
        foreach (var panel in panels)
        {
            DetachPanel(panel);
        }

        panels.Clear();
        foreach (var definition in definitions.Where(definition => definition is not null))
        {
            panels.Add(CreatePanelState(
                string.IsNullOrWhiteSpace(definition.Name) ? $"Panel {panels.Count + 1}" : definition.Name.Trim(),
                definition.CustomInstruction ?? string.Empty,
                definition.AccentColorHex,
                definition.Format));
        }

        if (panels.Count == 0)
        {
            panels.Add(CreatePanelState("Panel 1", "Answer with the perspective for this panel.", LeptaPanelAccentPalette.DefaultAccentColorHex, LeptaPanelFormats.Markdown));
        }
    }

    private ILeptaPanelState CreatePanelState(string name, string customInstruction, string? accentColorHex, string? format)
    {
        var panel = LeptaPanelStateFactory.Create(format, name, customInstruction, accentColorHex);
        AttachPanel(panel);
        return panel;
    }

    private void AttachPanel(ILeptaPanelState panel)
        => panel.PropertyChanged += HandlePanelPropertyChanged;

    private void DetachPanel(ILeptaPanelState panel)
        => panel.PropertyChanged -= HandlePanelPropertyChanged;

    private void HandlePanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is LeptaPanelStateBase panelState && e.PropertyName == nameof(LeptaPanelStateBase.RenderErrorMessage))
        {
            HandleMermaidRenderStateChanged(panelState);
        }

        if (e.PropertyName is nameof(ILeptaPanelState.Name) or nameof(ILeptaPanelState.CustomInstruction) or nameof(ILeptaPanelState.AccentColorHex))
        {
            PanelMetadataChanged?.Invoke();
        }
    }

    private void MovePanel(Guid panelId, int offset)
    {
        var currentIndex = panels
            .Select((panel, index) => new { panel, index })
            .FirstOrDefault(item => item.panel.Id == panelId)
            ?.index ?? -1;
        if (currentIndex < 0)
        {
            return;
        }

        MovePanelToIndex(panelId, currentIndex + offset);
    }

    private PanelResponseUpdatePump CreatePanelResponseUpdatePump(IEnumerable<ILeptaPanelState> panelSet)
        => new(panelSet.ToArray(), panelsView.ItemsControl.Dispatcher, logger);

    private static Task CompletePanelResponseUpdatePumpAsync(PanelResponseUpdatePump? updatePump)
        => updatePump?.CompleteAsync() ?? Task.CompletedTask;

    internal sealed class PanelResponseUpdatePump
    {
        private readonly ILeptaPanelState[] targetPanels;
        private readonly Dispatcher dispatcher;
        private readonly Channel<PanelResponseUpdate> channel;
        private readonly StringBuilder[] pendingBuffers;
        private readonly Task processingTask;
        private int completionRequested;

        public PanelResponseUpdatePump(
            ILeptaPanelState[] targetPanels,
            Dispatcher dispatcher,
            Shared.Diagnostics.ILeptaLogger logger)
        {
            this.targetPanels = targetPanels;
            this.dispatcher = dispatcher;
            channel = Channel.CreateUnbounded<PanelResponseUpdate>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            pendingBuffers = Enumerable.Range(0, targetPanels.Length)
                .Select(_ => new StringBuilder())
                .ToArray();
            processingTask = ProcessAsync(logger);
        }

        public void PostToken(int panelIndex, string? token)
        {
            if (string.IsNullOrEmpty(token)
                || (uint)panelIndex >= (uint)targetPanels.Length
                || Volatile.Read(ref completionRequested) != 0)
            {
                return;
            }

            channel.Writer.TryWrite(new PanelResponseUpdate(panelIndex, token));
        }

        public async Task CompleteAsync()
        {
            if (Interlocked.Exchange(ref completionRequested, 1) == 0)
            {
                channel.Writer.TryComplete();
            }

            await processingTask.ConfigureAwait(false);
        }

        private async Task ProcessAsync(Shared.Diagnostics.ILeptaLogger logger)
        {
            using var timer = new PeriodicTimer(PanelResponseFlushInterval);

            try
            {
                while (true)
                {
                    var tickTask = timer.WaitForNextTickAsync().AsTask();
                    await Task.WhenAny(tickTask, channel.Reader.Completion).ConfigureAwait(false);

                    DrainChannel();
                    await FlushPendingAsync().ConfigureAwait(false);

                    if (channel.Reader.Completion.IsCompleted && !HasPendingUpdates())
                    {
                        break;
                    }

                    if (tickTask.IsCompletedSuccessfully && !tickTask.Result)
                    {
                        break;
                    }
                }
            }
            catch (Exception exception) when (exception is TaskCanceledException or OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                logger.Log(nameof(LeptaController), $"Panel response update pump failed. reason={exception.Message}");
            }
            finally
            {
                DrainChannel();
                await FlushPendingAsync().ConfigureAwait(false);
            }
        }

        private void DrainChannel()
        {
            while (channel.Reader.TryRead(out var update))
            {
                pendingBuffers[update.PanelIndex].Append(update.Token);
            }
        }

        private bool HasPendingUpdates()
            => pendingBuffers.Any(buffer => buffer.Length > 0);

        public void FinalizePanel(int panelIndex, Action onCompleted)
        {
            if ((uint)panelIndex >= (uint)targetPanels.Length)
            {
                return;
            }

            DrainChannel();
            var pendingText = pendingBuffers[panelIndex].Length > 0
                ? pendingBuffers[panelIndex].ToString()
                : string.Empty;
            pendingBuffers[panelIndex].Clear();

            try
            {
                dispatcher.BeginInvoke(() =>
                {
                    if (!string.IsNullOrEmpty(pendingText))
                    {
                        targetPanels[panelIndex].Response += pendingText;
                    }

                    onCompleted();
                }, DispatcherPriority.Background);
            }
            catch (Exception exception) when (exception is InvalidOperationException && (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished))
            {
            }
        }

        private async Task FlushPendingAsync()
        {
            var batch = TakePendingBatch();
            if (batch.Count == 0)
            {
                return;
            }

            try
            {
                await dispatcher.InvokeAsync(() =>
                {
                    foreach (var update in batch)
                    {
                        if ((uint)update.PanelIndex >= (uint)targetPanels.Length)
                        {
                            continue;
                        }

                        targetPanels[update.PanelIndex].Response += update.Text;
                    }
                }, DispatcherPriority.Background);
            }
            catch (Exception exception) when (exception is TaskCanceledException or InvalidOperationException && (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished))
            {
            }
        }

        private List<PanelResponseBatchUpdate> TakePendingBatch()
        {
            var batch = new List<PanelResponseBatchUpdate>(targetPanels.Length);
            for (var i = 0; i < pendingBuffers.Length; i++)
            {
                if (pendingBuffers[i].Length == 0)
                {
                    continue;
                }

                batch.Add(new PanelResponseBatchUpdate(i, pendingBuffers[i].ToString()));
                pendingBuffers[i].Clear();
            }

            return batch;
        }
    }

    private readonly record struct PanelResponseUpdate(int PanelIndex, string Token);

    private readonly record struct PanelResponseBatchUpdate(int PanelIndex, string Text);
}

