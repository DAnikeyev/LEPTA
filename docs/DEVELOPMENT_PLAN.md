# LEPTA Development Plan

## Product Direction

LEPTA is a Windows desktop assistant for running one selected dashboard against one selected vLLM-compatible model. The core workflow is:

1. User configures or selects a model server.
2. User configures a dashboard made of multiple LEPTA panels, each with its own instruction.
3. User presses a global hotkey.
4. LEPTA reads the current clipboard text as the source document.
5. LEPTA sends parallel requests shaped as `document + general instruction + panel instruction`.
6. Each panel renders its response with Markdown, fenced code block support, and code highlighting.

The first implementation stages must assume that the vLLM server is already deployed and reachable on a known HTTP port. Docker-managed deployment should be planned, but implemented later after the external-server workflow is stable.

## Current Baseline

The repository already contains:

- A WPF application in `LEPTA`.
- vLLM HTTP, streaming, fallback, Docker compose, deployment, and memory-estimation services in `LEPTA.vLLM`.
- LEPTA panel orchestration that sends multiple panel requests in parallel.
- A global hotkey path that activates the window and runs LEPTA from clipboard.
- Basic Models, Chat, LEPTA, and Settings views in `MainWindow.xaml`.
- Theme resources and a UI style guide in `docs/UI_STYLE_GUIDE.md`.
- Tests for vLLM conversation behavior, fallback behavior, deployment service HTTP checks, and panel orchestration.

Important gaps:

- Runtime model configs and LEPTA presets are not persisted automatically to `%LOCALAPPDATA%\Lepta`.
- Preset save/load still uses file picker dialogs.
- Chat and LEPTA responses render plain text instead of Markdown and highlighted fenced code blocks.
- LEPTA panel editing is always inline and panels are fixed horizontal cards.
- Panels cannot be reordered.
- Navigation side panel cannot collapse to icons.
- Settings are incomplete and are not persisted.
- Action logs are only in embedded text/log controls, not in a transparent overlay.
- Docker deployment exists at service level but should be deferred until the external-server UX is reliable.

## Deeper Implementation Audit

This codebase is still prototype-quality. Some pieces are useful and should be preserved; other pieces should be repaired before more features are layered on top.

Working or mostly working pieces:

- `VllmChatCompletionClient` has useful OpenAI-compatible request support, streaming support, fallback parsing for content arrays, and tests around chat/completion fallback behavior.
- `VllmConversationService` and `LeptaRequestOrchestrator` already model the most important workflow: stream one request per panel in parallel using clipboard context plus instructions.
- `VllmDeploymentService` and `VllmDockerComposeBuilder` already generate compose files and translate common Docker errors. This is useful later, but should not drive the first UI milestone.
- The theme resource set and `ThemeController` are a reasonable base for light/dark UI work.
- Basic NLog file logging works and already writes under `%LOCALAPPDATA%\LEPTA\logs`.
- Non-integration tests currently pass with `dotnet test LEPTA.Tests/LEPTA.Tests.csproj --filter "Category!=Integration"`: 15 passed.

Broken, risky, or not implemented well:

- Full solution build can be blocked by a running `LEPTA.Tools.SandboxTests` process locking `LEPTA.vLLM.dll`. Agents should stop or exclude long-running sandbox tools before treating build failures as code failures.
- `LEPTA.Tools.SandboxTests` is in the solution and runs heavy Docker/benchmark code by default. It should be converted into an explicit smoke/benchmark tool that never runs unintentionally during normal build/test flows.
- The main WPF window is tightly coupled to controller constructors with many direct control references. New work should extract state and persistence into services/view models instead of adding more parameters.
- `ModelsController` keeps model profiles only in memory. Edits are lost after app restart.
- `LeptaController` keeps panels only in memory. Presets use `SaveFileDialog`/`OpenFileDialog`, which conflicts with the required `%LOCALAPPDATA%\Lepta` storage model.
- `LeptaController` rejects Docker-managed profiles for LEPTA requests, which is acceptable for the first milestone but must remain clearly communicated in the UI.
- Hotkey settings are UI state only and are not persisted. Hotkey registration result is not surfaced, so conflicts can fail silently.
- The app uses `Clipboard.GetText()` directly as the document source. That is acceptable for the first milestone, but the UI should call it "Clipboard" consistently unless a real document/source capture feature is added.
- Logging bootstrapper allocates or attaches a console for the WPF app. This is useful during development but undesirable for a polished desktop app; production logging should not open an extra console window.
- Integration tests in `UnitTest1.cs` rely on a live vLLM server at `localhost:8512` unless filtered out. They should be kept behind explicit categories and documented commands.
- Target framework is `net10.0` / `net10.0-windows`. That currently builds on this machine, but future agents should verify whether this is intentional or whether the app should target an LTS runtime.

## Architecture Principles

- Keep the external HTTP vLLM path working throughout all stages.
- Treat `%LOCALAPPDATA%\Lepta` as the only user data root. Do not ask users to choose folders for app state.
- Keep UI colors and brushes theme-driven through existing theme resources.
- Prefer testable services for persistence, Markdown parsing, settings, hotkeys, and logging instead of growing `MainWindow.xaml.cs`.
- Use view models for new UI-heavy work before adding more controller constructor parameters.
- Keep Chat and LEPTA result rendering on one shared rendering component/service so Markdown behavior stays consistent.
- Keep Docker-managed deployment isolated behind existing vLLM service abstractions and make it a later stage.

## Data Storage Plan

Use this app data root:

`%LOCALAPPDATA%\Lepta`

Proposed structure:

- `%LOCALAPPDATA%\Lepta\settings.json`
- `%LOCALAPPDATA%\Lepta\models\model-configs.json`
- `%LOCALAPPDATA%\Lepta\presets\*.lepta.json`
- `%LOCALAPPDATA%\Lepta\dashboards\*.dashboard.json`
- `%LOCALAPPDATA%\Lepta\vllm\*.compose.yml`
- `%LOCALAPPDATA%\Lepta\logs\lepta.log`

Persisted data should be versioned with a small schema version field so later agents can migrate safely.

## Repair Gate - Before New Features

Goal: make the rough current implementation safe enough for iterative feature agents.

Scope:

- Confirm normal commands:
  - `dotnet test LEPTA.Tests/LEPTA.Tests.csproj --filter "Category!=Integration"`,
  - `dotnet build LEPTA.sln` after stopping any running sandbox tool that locks output DLLs.
- Move or configure `LEPTA.Tools.SandboxTests` so normal solution builds are not blocked by long-running benchmark tooling.
- Rename or reorganize misleading test files, especially `UnitTest1.cs`, so unit tests, integration tests, smoke tests, and benchmarks are clearly separated.
- Add a short `docs/DEVELOPMENT_COMMANDS.md` with supported build/test/smoke commands.
- Decide whether `net10.0` is intentional. If not, retarget to the selected supported runtime before feature work grows.
- Stop opening a console window from the WPF app by default. Keep console logging only behind a development flag if needed.
- Add a small `AppDataPaths` or equivalent service so all future persistence uses one path policy.
- Add a small validation layer for model server profiles before making UI changes.

Acceptance criteria:

- Normal unit tests pass without a live vLLM server.
- Normal solution build succeeds when no app/tool process is holding DLL locks.
- Benchmark/smoke tooling is opt-in and documented.
- WPF startup does not create an unexpected console window in normal mode.
- Future agents have clear commands and a clear persistence root to build on.

## Stage 0 - Stabilize The External vLLM Workflow

Goal: make the current already-deployed-server workflow reliable before any Docker-managed work.

Scope:

- Audit the existing `ModelsController`, `ChatController`, `LeptaController`, `VllmDeploymentService`, `VllmConversationService`, and `LeptaRequestOrchestrator` flows.
- Make a clear distinction in UI copy between `Already deployed HTTP server` and future `Deploy local folder with Docker`.
- Ensure the default model profile points to `http://localhost:8512` and can be edited.
- Ensure Chat and LEPTA both resolve the served model from `/v1/models`.
- Keep the current global hotkey behavior: restore window, activate it, run LEPTA from clipboard.
- Add user-facing validation for empty endpoint, invalid URI, unreachable endpoint, and empty model list.
- Add cancellation behavior for in-flight LEPTA generation if a second run is triggered, or make the rejection state clear.
- Surface hotkey registration errors or conflicts in Settings.
- Make all copy say "clipboard" until a real document/source capture feature exists.
- Keep Docker-managed profile actions hidden, disabled, or clearly marked as later-stage behavior for LEPTA/Chat.

Acceptance criteria:

- User can add or edit an already deployed HTTP server address.
- User can test the server.
- Chat can send a prompt to the selected server.
- LEPTA can send parallel panel requests to the selected server from clipboard.
- Errors are visible without crashing the app.
- Hotkey conflict/failure is visible to the user.

Recommended tests:

- Unit tests for endpoint normalization and validation.
- Controller-level tests where practical, or extracted service tests for server profile validation.
- Manual test with a live local vLLM server on `localhost:8512`.
- Manual hotkey conflict test with an already registered shortcut.

## Stage 1 - Local App Data Persistence

Goal: save and load all user-owned configuration without asking for folders.

Scope:

- Add an app data service responsible for creating `%LOCALAPPDATA%\Lepta` and child folders.
- Add JSON persistence services for:
  - app settings,
  - model server configs,
  - LEPTA presets,
  - dashboard definitions.
- Replace preset save/load dialogs with in-app preset list actions:
  - Save current preset,
  - Save as new preset,
  - Load preset,
  - Delete preset.
- Persist model configs when edited and reload them on startup.
- Persist hotkey settings and theme setting.
- Add schema version fields to persisted files.
- Normalize app folder casing to the required `%LOCALAPPDATA%\Lepta`. Existing uppercase `%LOCALAPPDATA%\LEPTA` log paths may be migrated or supported during transition, but new code should use one canonical root.
- Add import/export later only if explicitly needed; do not make it part of the core path.

Acceptance criteria:

- Closing and reopening LEPTA preserves servers, selected server, theme, hotkey, dashboard, panels, and instructions.
- User can save and delete presets from inside the app.
- No file picker is required for normal save/load/delete flows.
- Corrupt JSON is handled with a clear error and backup/ignore behavior.

Recommended tests:

- Unit tests for path creation under a fake app data root.
- Round-trip serialization tests for settings, model configs, presets, and dashboards.
- Migration/defaulting tests for missing optional fields.

## Stage 2 - Dashboard And Panel Model

Goal: make the LEPTA workspace a real dashboard that can be selected, saved, and rearranged.

Scope:

- Introduce dashboard models separate from transient panel response state.
- Do not persist generated responses as part of dashboard definitions unless a later explicit history feature is requested.
- A dashboard should contain:
  - dashboard id,
  - name,
  - general instruction,
  - selected server id or endpoint reference,
  - ordered panel list,
  - panel name,
  - panel custom instruction,
  - panel display settings if needed later.
- Add dashboard selector in the LEPTA header.
- Add plus button for panels.
- Add edit controls near panel title for name and custom instruction.
- Add delete control near panel title.
- Add reorder support for panels. Start with simple Move left / Move right buttons if drag-and-drop is too risky; drag-and-drop can be a follow-up enhancement.
- Keep panel response state separate so editing instructions does not accidentally persist generated text.

Acceptance criteria:

- User can select a dashboard.
- User can create, rename, save, and delete dashboards.
- User can add, edit, delete, and reorder panels.
- Dashboard panel order persists across restarts.
- LEPTA generation uses the selected dashboard's general instruction and panel instructions.

Recommended tests:

- Unit tests for dashboard persistence and panel ordering.
- Unit tests for building panel requests from a dashboard.
- Manual UI test for add/edit/delete/reorder flows.

## Stage 3 - Markdown And Code Rendering

Goal: render Chat and LEPTA responses as readable Markdown with highlighted fenced code blocks.

Scope:

- Choose a WPF-compatible Markdown rendering approach.
- Support:
  - paragraphs,
  - headings,
  - bold/italic,
  - ordered and unordered lists,
  - links,
  - inline code,
  - fenced code blocks,
  - language identifiers on fenced code blocks,
  - syntax highlighting for common languages.
- Build one shared response renderer used by both Chat and LEPTA panels.
- Prefer a renderer adapter that can be unit-tested without launching WPF where possible.
- Preserve streamed response behavior:
  - during streaming, show text incrementally,
  - after completion, render final Markdown,
  - optionally debounce partial Markdown rendering if performance is acceptable.
- Add copy buttons for full response and individual code blocks.
- Ensure rendered content uses theme resources for both light and dark themes.

Acceptance criteria:

- Chat messages render Markdown after each assistant response completes.
- LEPTA panels render Markdown after each panel response completes.
- Fenced code blocks are visually distinct and syntax-highlighted.
- Triple-backtick code blocks do not break the surrounding layout.
- Large responses remain scrollable and responsive.

Recommended tests:

- Unit tests for Markdown parsing boundaries if a wrapper service is introduced.
- Snapshot-style tests for renderer model output if UI automation is too heavy.
- Manual tests for C#, JSON, XML/XAML, PowerShell, Markdown, and plain text code blocks.

## Stage 4 - Responsive LEPTA Workspace Layout

Goal: make panels take most of the window and make navigation compactable.

Scope:

- Redesign the window layout so LEPTA panels are the main content.
- Treat the current `MainWindow.xaml` as a prototype layout. Large UI changes should move toward smaller user controls or view-specific components instead of expanding the single XAML file indefinitely.
- Move general instruction into a header button/dialog instead of always taking vertical space.
- Add a header with:
  - selected dashboard,
  - selected server,
  - run button,
  - general instructions button,
  - settings/log status affordance if needed.
- Make navigation side panel togglable:
  - expanded mode shows labels,
  - collapsed mode shows icons/symbols,
  - state persists.
- Replace fixed panel sizes with responsive sizing:
  - panel grid wraps or stretches based on available width,
  - panel content owns most vertical space,
  - scrollbars appear inside panel responses, not around the entire app unless necessary.
- Improve small-window behavior down to the existing minimum size.
- Follow `docs/UI_STYLE_GUIDE.md` for resource usage and contrast.

Acceptance criteria:

- LEPTA panels occupy most of the main window.
- Side navigation can collapse to symbols and expand back.
- Header controls remain usable at minimum window size.
- Panels remain readable in dark and light themes.
- No important controls are clipped at the minimum window size.

Recommended tests:

- Manual resize test at minimum size, common laptop size, and large desktop size.
- Manual dark/light theme test.
- UI review against `docs/UI_STYLE_GUIDE.md`.

## Stage 5 - Settings And Action Log Overlay

Goal: centralize settings and optionally show action logs in a transparent overlay.

Scope:

- Add settings model and persistence for:
  - theme,
  - hotkey,
  - collapsed navigation,
  - enable action log overlay,
  - verbose vLLM logs,
  - default dashboard,
  - default server.
- Add a log event stream abstraction that UI controllers and services can publish to.
- Keep file/runtime logger behavior, but separate developer diagnostics from user-facing action events before rendering logs in the overlay.
- Add transparent bottom-right overlay:
  - visible only when enabled,
  - non-blocking,
  - newest messages visible,
  - auto-fade or capped list,
  - uses theme resources,
  - does not steal focus.
- Add clear visual distinction between normal action logs, warnings, and errors.

Acceptance criteria:

- User can toggle action log overlay in Settings.
- Overlay appears in bottom-right and does not block normal usage.
- Key actions appear in overlay: server test, chat send, LEPTA run, panel completion, preset/dashboard save/delete, deployment actions.
- Setting persists across restart.

Recommended tests:

- Unit tests for settings persistence.
- Unit tests for log event filtering/capping.
- Manual overlay test during Chat, LEPTA generation, and server validation.

## Stage 6 - Chat Experience Improvements

Goal: make Chat a useful model testing surface, not just a smoke test.

Scope:

- Keep Chat tied to selected HTTP vLLM server.
- Render assistant responses with the shared Markdown renderer.
- Add a configurable system instruction for Chat.
- Persist chat settings, but avoid persisting full conversations unless explicitly required later.
- Add response metadata:
  - model,
  - elapsed time,
  - token count when available,
  - fallback mode if used.
- Add stop/cancel button for streaming responses.
- Keep Enter-to-send behavior, and support Shift+Enter for newline if desired.

Acceptance criteria:

- User can test a deployed model through Chat with readable rendered output.
- Long responses can be cancelled.
- Response metadata is visible but not visually dominant.

Recommended tests:

- Unit tests for cancellation behavior in extracted services.
- Manual streaming, cancellation, and Markdown rendering tests.

## Stage 7 - Docker-Managed vLLM Deployment

Goal: implement local model deployment after external-server workflows are stable.

Scope:

- Use the existing `VllmDockerComposeBuilder` and `VllmDeploymentService` as the base.
- Before UI-enabling Docker deployment, add service-level tests for compose output and validation. The current compose/deploy implementation is a starting point, not a finished product.
- Add model profile fields required for Docker deployment:
  - local Hugging Face folder path,
  - optional Hugging Face model id,
  - Docker image,
  - host port,
  - dtype,
  - GPU memory utilization,
  - max model length,
  - KV cache dtype,
  - tensor parallel size,
  - CPU offload,
  - max parallel sequences,
  - quantization settings,
  - verbose logs.
- Validate before deployment:
  - Docker CLI available,
  - Docker engine running,
  - port is available or user is warned,
  - local model folder exists when local path mode is used,
  - container name is valid and unique.
- On deploy:
  - assemble compose YAML,
  - save it under `%LOCALAPPDATA%\Lepta\vllm`,
  - run `docker compose up -d`,
  - poll `/v1/models`,
  - update status and logs.
- Keep alternative server-add flow for already deployed HTTP addresses.
- Add stop/restart actions only for profiles managed by LEPTA.

Acceptance criteria:

- User can select a local model folder, configure deployment settings, and press Deploy.
- A proper compose file is generated and saved under `%LOCALAPPDATA%\Lepta\vllm`.
- vLLM starts on the selected host port.
- LEPTA can test and use the deployed server after it becomes reachable.
- Already deployed HTTP server profiles continue to work.

Recommended tests:

- Unit tests for compose builder output across configuration combinations.
- Unit tests for validation logic.
- Manual integration test with Docker Desktop and a small compatible model.

## Stage 8 - Polish, Accessibility, And Reliability

Goal: make the app feel consistent, responsive, and resilient.

Scope:

- Add empty states for dashboards, panels, servers, and chat.
- Add loading states and disable/enable rules for long actions.
- Add keyboard navigation for common actions.
- Add accessible labels/tooltips for icon-only navigation and buttons.
- Review focus behavior when dialogs open/close.
- Confirm all UI uses theme resources.
- Add graceful shutdown handling for in-flight requests.
- Add centralized error presentation rules.
- Review memory and UI responsiveness under large clipboard content and multiple panels.

Acceptance criteria:

- App remains responsive during multi-panel generation.
- Icon-only controls have accessible names/tooltips.
- Errors are actionable and not duplicated noisily.
- Light and dark themes remain readable.
- Large clipboard input does not freeze the UI.

Recommended tests:

- Manual accessibility pass with keyboard-only navigation.
- Manual large-clipboard test.
- Manual multi-panel test with at least 6 panels.
- Manual light/dark contrast pass.

## Suggested Agent Execution Order

Future AI agents should execute stages in this order:

1. Repair gate: build/test/tooling cleanup and current implementation stabilization.
2. Stage 0: stabilize external HTTP vLLM workflow.
3. Stage 1: app data persistence.
4. Stage 2: dashboard and panel model.
5. Stage 3: Markdown and code rendering.
6. Stage 4: responsive workspace and collapsible navigation.
7. Stage 5: settings and log overlay.
8. Stage 6: Chat improvements.
9. Stage 7: Docker-managed deployment.
10. Stage 8: polish, accessibility, and reliability.

Each agent should keep changes small and reviewable. If a stage is too large, split it into service/model work first, then UI binding, then tests.

## First Implementation Target

Start with only these shipped behaviors:

- Normal build/test commands are documented and reliable.
- User can add/select an already deployed HTTP vLLM server by address.
- User can select a dashboard.
- User can configure general instruction and panel instructions.
- User can press the hotkey to run parallel LEPTA requests from clipboard.
- Chat and LEPTA can call the selected existing server.
- Presets, dashboards, model configs, hotkey, and theme settings persist under `%LOCALAPPDATA%\Lepta`.

Do not start Docker-managed local deployment until the above path is tested and stable.
