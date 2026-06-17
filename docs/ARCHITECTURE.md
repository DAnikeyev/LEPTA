# LEPTA architecture overview

This document explains how the current LEPTA codebase is organized and how the main workflows move through the system.

## 1. Product shape

LEPTA is a WPF desktop application centered around two main experiences:

1. **Chat**: a conversational interface for testing a selected vLLM-compatible server.
2. **LEPTA**: a dashboard that fans one source text out into multiple parallel panel requests.

The current implementation is intentionally biased toward the **external HTTP server workflow**:

- user selects an already deployed HTTP server,
- LEPTA probes `/v1/models`,
- resolves the served model name,
- sends chat or panel requests.

Docker-managed deployment is present as a service capability, but it is not yet the primary runtime path for Chat and LEPTA generation.

## 2. Solution layout

### `LEPTA/`
The WPF desktop application.

Contains:

- `MainWindow.xaml` and `MainWindow.xaml.cs`
- controllers for models, chat, LEPTA, and theming
- reusable UI controls such as `MarkdownResponseView`
- rendering and notification services

Key responsibility: user interaction, state binding, and orchestration of services into a desktop workflow.

### `LEPTA.Shared/`
Cross-project shared contracts and persistence pieces.

Contains:

- app settings models
- dashboard and panel models
- preset models
- diagnostics interfaces and action log types
- app-data path policy and JSON stores

Key responsibility: shared state and storage policy.

### `LEPTA.vLLM/`
vLLM-specific integration and orchestration logic.

Contains:

- HTTP client logic for chat/completions
- conversation service with streaming and fallback handling
- LEPTA panel orchestration
- server probing and validation
- Docker compose generation and deployment helpers

Key responsibility: everything needed to talk to or manage a vLLM-style backend.

### `LEPTA.Infrastructure/`
General supporting infrastructure referenced by the app.

### `LEPTA.Tests/`
NUnit coverage for:

- prompt construction,
- streaming/fallback logic,
- probing and deployment validation,
- app-data persistence,
- action log behavior.

## 3. Main runtime flows

## 3.1 Startup and persisted state

On startup, `MainWindow` loads persisted data and wires controllers together.

Important persisted sources include:

- app settings
- dashboards
- server configurations
- presets

The shared path policy is defined by `LEPTA.Shared/Services/AppDataPaths.cs`, which uses:

- `%LOCALAPPDATA%\Lepta`

The application ensures the required directories exist before saving state.

## 3.2 Model/server flow

Server profiles are managed by `ModelsController`.

A profile can represent either:

- an already deployed HTTP server, or
- a Docker-managed deployment configuration.

For an HTTP profile, LEPTA validates and probes the endpoint through `VllmDeploymentService`.

Probe flow:

1. normalize endpoint,
2. call `GET {endpoint}/v1/models`,
3. validate JSON payload,
4. extract available model ids,
5. use the first served model as the runtime target.

This same probe behavior is reused by both Chat and LEPTA panel runs.

## 3.3 Chat flow

`ChatController` drives the Chat screen.

Current flow:

1. user selects a server profile,
2. controller ensures it is an `Already deployed HTTP server` profile,
3. server is probed via `/v1/models`,
4. model name is resolved,
5. prompt is sent through `VllmConversationService.StreamConversationAsync`,
6. tokens are streamed into the UI,
7. final content is rendered as Markdown,
8. response metadata is shown in the chat bubble.

### Fallback behavior

`VllmConversationService` first tries chat-style requests at:

- `/v1/chat/completions`

If the server rejects chat payloads with certain HTTP error patterns, the service falls back to:

- `/v1/completions`

This makes LEPTA more tolerant of vLLM-compatible deployments with different chat-template behavior.

## 3.4 LEPTA dashboard flow

`LeptaController` drives the panel dashboard experience.

A dashboard contains:

- dashboard id and name,
- selected server id,
- general instruction,
- thinking toggle,
- ordered panel list.

Each panel contains:

- panel name,
- custom instruction,
- accent color,
- output format.

### Run sequence

When a LEPTA run starts:

1. the current clipboard text is captured,
2. the selected server is validated,
3. `/v1/models` is probed,
4. the first served model id is resolved,
5. one `LeptaPanelRequest` is created per panel,
6. `LeptaRequestOrchestrator.GenerateForPanelsAsync` is called,
7. panel tokens are streamed back into the matching panel state,
8. final text is rendered in each panel.

### Prompt structure

The orchestrator builds prompts in a fixed order:

1. `System Instructions`
2. `Global Instructions`
3. `Text`
4. `Panel Instructions`
5. `Response`

This structure is tested directly in `LEPTA.Tests/LeptaRequestOrchestratorTests.cs`.

### Parallelism

All panel requests are launched in parallel using `Task.WhenAll`, so one clipboard capture can generate several perspectives at once.

### Shared-prefix warming

When enabled, LEPTA can warm a shared prompt prefix before running multiple panels. The orchestrator uses a `cache_salt` to keep related requests associated.

### Document trimming

Large clipboard input is trimmed to an estimated token-safe size before sending. The orchestrator currently uses a 6000-token estimate with a simple character-based cap.

## 3.5 Markdown and response rendering

`MarkdownResponseView` is the reusable response surface used by the UI.

Rendering behavior:

- while streaming: plain incremental text is shown,
- after completion: final content is rendered as formatted Markdown.

The renderer supports:

- headings,
- paragraphs,
- emphasis,
- lists,
- quotes,
- links,
- inline code,
- fenced code blocks,
- syntax-highlighted code,
- copy actions,
- Mermaid rendering when panel format requests it.

Implementation lives in `LEPTA/Services/MarkdownResponseRenderer.cs` and uses `Markdig`.

## 3.6 Settings, logs, and overlay

The application persists settings through `AppSettingsStore`.

Notable settings include:

- theme,
- action log overlay toggle,
- verbose vLLM logs,
- font sizes,
- default dashboard,
- default server,
- hotkey,
- chat settings,
- LEPTA settings,
- LEPTA system instructions.

An in-memory action log stream collects recent events. `MainWindow` can mirror those events into a bottom-right overlay for lightweight runtime visibility.

## 4. Storage model

The current storage layout is:

- `%LOCALAPPDATA%\Lepta\settings.json`
- `%LOCALAPPDATA%\Lepta\models\model-configs.json`
- `%LOCALAPPDATA%\Lepta\dashboards\*.dashboard.json`
- `%LOCALAPPDATA%\Lepta\presets\*.lepta.json`
- `%LOCALAPPDATA%\Lepta\vllm\...`
- `%LOCALAPPDATA%\Lepta\logs\...`

The stores are intentionally simple JSON file stores, which makes the app easy to inspect and back up.

## 5. Docker deployment path

The Docker deployment path is implemented mostly inside `VllmDeploymentService` and related types.

Capabilities already present include:

- validation of deployment settings,
- Docker availability checks,
- compose generation,
- `docker compose up -d`,
- wait-for-readiness probing through `/v1/models`,
- optional log collection when verbose logging is enabled.

However, from a product point of view, this path should still be treated as **secondary to the external HTTP workflow** until the end-user UX is fully stabilized.

## 6. Testing strategy

The test suite currently gives good coverage over the most important non-UI behaviors.

Examples:

- prompt ordering and document trimming
- chat-to-completions fallback behavior
- streaming token handling
- thinking flag propagation
- server probing results
- deployment validation and readiness waiting
- app-data persistence and corrupt-file recovery
- action log event capping and publication

Integration tests exist separately and are intentionally filtered out during normal test runs.

## 7. Current architectural strengths

- clear separation between shared models, UI, and vLLM services
- resilient request path with chat/completions fallback
- explicit app-data path policy
- reusable Markdown response surface
- test coverage for many service-level edge cases

## 8. Current architectural constraints

- `MainWindow.xaml.cs` is still large and central
- controllers are strongly tied to concrete WPF controls
- Chat and LEPTA intentionally reject Docker-managed profiles at runtime for now
- some UI polish and workflow unification are still roadmap work

## 9. Best mental model for contributors

Think of LEPTA as three layers:

1. **Desktop shell and controllers** in `LEPTA/`
2. **Shared state and persistence** in `LEPTA.Shared/`
3. **Model communication and deployment services** in `LEPTA.vLLM/`

If you keep those layers separate, the project stays easier to extend and test.

