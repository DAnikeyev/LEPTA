# LEPTA scenarios and walkthroughs

This document describes practical ways to use LEPTA and explains what each workflow is intended to achieve.

## Scenario 1: Connect an existing vLLM server and verify it with Chat

Use this scenario when you already have a server running and want to confirm that LEPTA can talk to it.

### Goal
Verify connectivity and confirm the model resolves from `/v1/models`.

### Typical steps
1. Open the **Models** view.
2. Select or create a profile using **Already deployed HTTP server**.
3. Enter the server address, for example `http://localhost:8512`.
4. Save/select that profile.
5. Open **Chat**.
6. Select the same server profile.
7. Send a short prompt.

### What LEPTA does
- validates the endpoint,
- probes `/v1/models`,
- resolves the first served model id,
- streams the response,
- renders the final answer as Markdown.

### Why this scenario matters
This is the fastest way to separate **server connectivity issues** from **dashboard workflow issues**.

## Scenario 2: Analyze one source document from multiple perspectives

This is the main LEPTA scenario.

### Goal
Turn one copied document into multiple focused outputs in parallel.

### Example dashboard
- `Summary`
- `Key risks`
- `Action items`
- `Open questions`

### Typical steps
1. Copy source text to the clipboard.
2. Open the **LEPTA** view.
3. Select a dashboard.
4. Select an HTTP server profile.
5. Review the general instruction.
6. Review or edit each panel instruction.
7. Start the run.

### What LEPTA does
- reads clipboard text,
- resolves the current server and model,
- builds one shared prompt prefix,
- creates one request per panel,
- streams each response independently,
- shows all outputs side by side.

### Good use cases
- summarizing technical documents,
- reviewing meeting notes,
- extracting tasks from requirements,
- analyzing source code or patches,
- preparing interview or study notes.

## Scenario 3: Use the global hotkey as a rapid capture workflow

This scenario is useful when you want LEPTA to behave like a reusable analysis shortcut.

### Goal
Avoid switching back into the app manually every time.

### Typical steps
1. Configure the hotkey in settings.
2. Copy text from another application.
3. Press the global hotkey.
4. Let LEPTA activate and run the dashboard from clipboard.

### Default hotkey
By default, the stored settings model uses:

- `Ctrl+Shift+F8`

### Best fit
- fast code review notes,
- summarizing browser content,
- checking logs or stack traces,
- turning copied text into structured analysis.

## Scenario 4: Build reusable dashboards for recurring work

A dashboard is useful when you repeat the same style of analysis often.

### Goal
Create a repeatable workspace instead of rewriting prompts every time.

### Example dashboard types

#### Engineering review dashboard
- `Architecture`
- `Risks`
- `Test ideas`
- `Refactor suggestions`

#### Product discovery dashboard
- `Summary`
- `User pain points`
- `Open questions`
- `Proposed next steps`

#### Learning dashboard
- `Core concepts`
- `Terminology`
- `Examples`
- `What to study next`

### Benefit
The clipboard changes, but the analytical structure stays reusable.

## Scenario 5: Save a preset for panel layouts you want to reuse quickly

Presets are useful when you want to store a specific LEPTA setup independently from ad hoc editing.

### Goal
Reuse a known-good panel arrangement and instruction set.

### What is persisted
A preset can capture:

- general instruction,
- thinking preference,
- panel list,
- panel names,
- panel instructions,
- panel formats.

### When it helps
- team-standard analysis templates,
- repeated code review flows,
- repeatable documentation extraction tasks.

## Scenario 6: Render technical answers with Markdown and code blocks

This scenario is especially useful for engineering workflows.

### Goal
Make generated responses easier to scan and reuse.

### Supported ideas in the current app
- Markdown formatting
- fenced code blocks
- syntax-highlighted code rendering
- copy full response
- copy individual code blocks
- Mermaid-oriented panel rendering

### Good fits
- generated PowerShell or C# snippets,
- architecture notes with diagrams,
- checklist-style outputs,
- documentation drafting.

## Scenario 7: Use Chat after a LEPTA panel gives a useful result

The app supports seeding Chat from a LEPTA response so you can continue exploring an answer.

### Goal
Go from broad parallel analysis to a focused follow-up conversation.

### Example
1. Run LEPTA over copied text.
2. Find the most useful panel result.
3. Open that result in Chat.
4. Continue asking follow-up questions.

### Why this is helpful
LEPTA is optimized for breadth, while Chat is better for depth.

## Scenario 8: Prepare a future local Docker deployment profile

This scenario is partly implemented and should be understood as a staged capability.

### Goal
Describe how LEPTA models the future local-deployment path.

### Current reality
The codebase already contains service-level support for:

- validating Docker availability,
- generating compose files,
- starting deployments,
- waiting for `/v1/models` readiness.

### Important caveat
For day-to-day runtime use today, **Chat and LEPTA generation still center on externally reachable HTTP profiles**.

That means Docker profile management exists, but it should be treated as an implementation capability that is still being refined into the primary UX.

## Scenario 9: Use LEPTA as a structured study assistant

A strong non-coding scenario is turning learning material into a reusable dashboard.

### Example panel set
- `Summary`
- `Definitions`
- `Mechanisms`
- `Questions to revisit`

### Source material examples
- copied article text,
- lecture notes,
- API docs,
- RFC excerpts,
- tutorial content.

## Scenario 10: Use LEPTA for quick incident triage

### Example clipboard sources
- logs,
- exception traces,
- deployment errors,
- alert summaries.

### Example panel set
- `Likely root cause`
- `Immediate actions`
- `Missing evidence`
- `Communication summary`

This pattern works well because it turns one messy input into several operational viewpoints quickly.

## Choosing the right workflow

### Prefer Chat when
- you want a conversational follow-up,
- you are testing server connectivity,
- you want one answer instead of several panel outputs.

### Prefer LEPTA when
- you want multiple perspectives at once,
- you already know the output categories you care about,
- you want a repeatable dashboard-based workflow,
- you are working from copied source text.

## Practical tips

- Keep panel instructions narrow and distinct so responses do not overlap.
- Use dashboards for recurring tasks instead of rebuilding layouts each time.
- Verify a new server in Chat before relying on it for multi-panel runs.
- Treat the HTTP server workflow as the most reliable path in the current version.

