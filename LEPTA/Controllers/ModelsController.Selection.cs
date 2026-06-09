using LEPTA.Shared.Diagnostics;
using LEPTA.vLLM.Models;

namespace LEPTA.Controllers;

internal sealed partial class ModelsController
{
    public void SelectServer(string? serverId)
    {
        var server = string.IsNullOrWhiteSpace(serverId)
            ? servers.FirstOrDefault()
            : servers.FirstOrDefault(item => string.Equals(item.Id, serverId, StringComparison.OrdinalIgnoreCase))
              ?? servers.FirstOrDefault();

        activeServer = server;
        SynchronizeSelectedServer(server);
        if (server is not null)
        {
            LoadConfiguration(server);
        }
        else
        {
            ClearConfigurationInputs();
        }

        UpdateActionButtons();
    }

    public void HandleModelsSelectionChanged()
    {
        if (isSynchronizingSelection)
        {
            return;
        }

        if (selection.ModelsList.SelectedItem is VllmServerConfiguration server)
        {
            logger.Log(nameof(ModelsController), $"Model selection changed to '{server.Name}'. endpoint={server.Endpoint}.");
            SelectServer(server.Id);
            OnStateChanged();
        }
    }

    public void HandleChatServerSelectionChanged()
    {
        if (isSynchronizingSelection)
        {
            return;
        }

        if (selection.ChatServerCombo.SelectedItem is VllmServerConfiguration server)
        {
            logger.Log(nameof(ModelsController), $"Chat server selection changed to '{server.Name}'. endpoint={server.Endpoint}.");
            SelectServer(server.Id);
            OnStateChanged();
        }
    }

    public void AddModel()
    {
        var server = new VllmServerConfiguration
        {
            Name = $"HTTP server {servers.Count + 1}",
            UseExistingHttpServer = true,
            HostPort = 8512,
            HttpServerAddress = "http://localhost:8512",
            EnableVerboseLogs = IsVerboseVllmLogsEnabled
        };

        servers.Add(server);
        ResetServerStatus(server);
        RefreshConnectedServers();
        SelectServer(server.Id);
        logger.Log(nameof(ModelsController), $"Added model profile '{server.Name}' with endpoint {server.Endpoint}.");
        OnStateChanged();
    }

    public void DeleteSelectedModel()
    {
        if (IsBusy)
        {
            return;
        }

        var selectedServer = SelectedServer;
        if (selectedServer is null)
        {
            return;
        }

        var removedIndex = servers.IndexOf(selectedServer);
        if (removedIndex < 0)
        {
            return;
        }

        servers.RemoveAt(removedIndex);
        connectedServers.Remove(selectedServer);

        var nextServer = servers.Count == 0
            ? null
            : servers[Math.Clamp(removedIndex, 0, servers.Count - 1)];

        SelectServer(nextServer?.Id);

        logger.Log(nameof(ModelsController), $"Deleted model profile '{selectedServer.Name}'.");
        PublishAction($"Deleted model profile '{selectedServer.Name}'.", ActionLogLevel.Warning);
        OnStateChanged();
    }

    private void SynchronizeSelectedServer(VllmServerConfiguration? server)
    {
        if (isSynchronizingSelection)
        {
            return;
        }

        isSynchronizingSelection = true;
        try
        {
            if (!ReferenceEquals(selection.ModelsList.SelectedItem, server))
            {
                selection.ModelsList.SelectedItem = server;
            }

            if (server is null)
            {
                if (selection.ChatServerCombo.SelectedItem is not null)
                {
                    selection.ChatServerCombo.SelectedItem = null;
                }
            }
            else if (connectedServers.Contains(server)
                     && !ReferenceEquals(selection.ChatServerCombo.SelectedItem, server))
            {
                selection.ChatServerCombo.SelectedItem = server;
            }
        }
        finally
        {
            isSynchronizingSelection = false;
        }
    }

    private void InitializeServerStatuses()
    {
        foreach (var server in servers)
        {
            ResetServerStatus(server);
        }
    }

    private void RefreshConnectedServers()
    {
        var selectedChatServerId = (selection.ChatServerCombo.SelectedItem as VllmServerConfiguration)?.Id;
        var selectedModelServerId = SelectedServerId;
        var readyServers = servers.Where(server => server.HasEstablishedConnection).ToList();

        for (var index = connectedServers.Count - 1; index >= 0; index--)
        {
            if (!readyServers.Contains(connectedServers[index]))
            {
                connectedServers.RemoveAt(index);
            }
        }

        for (var index = 0; index < readyServers.Count; index++)
        {
            var server = readyServers[index];
            if (index < connectedServers.Count && ReferenceEquals(connectedServers[index], server))
            {
                continue;
            }

            var existingIndex = connectedServers.IndexOf(server);
            if (existingIndex >= 0)
            {
                connectedServers.Move(existingIndex, index);
            }
            else
            {
                connectedServers.Insert(index, server);
            }
        }

        var preferredServer = readyServers.FirstOrDefault(server => string.Equals(server.Id, selectedChatServerId, StringComparison.OrdinalIgnoreCase))
            ?? readyServers.FirstOrDefault(server => string.Equals(server.Id, selectedModelServerId, StringComparison.OrdinalIgnoreCase));

        if (preferredServer is null)
        {
            if (selection.ChatServerCombo.SelectedItem is not null && connectedServers.Count == 0)
            {
                isSynchronizingSelection = true;
                try
                {
                    selection.ChatServerCombo.SelectedItem = null;
                }
                finally
                {
                    isSynchronizingSelection = false;
                }
            }

            return;
        }

        if (!ReferenceEquals(selection.ChatServerCombo.SelectedItem, preferredServer))
        {
            isSynchronizingSelection = true;
            try
            {
                selection.ChatServerCombo.SelectedItem = preferredServer;
            }
            finally
            {
                isSynchronizingSelection = false;
            }
        }
    }
}
