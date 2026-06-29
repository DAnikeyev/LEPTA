# Changelog

## [0.2.1] - 2026-06-29
- Fixed chat continuation from LEPTA panels becoming unresponsive when seeded prompts or responses grew too large for the selected model context.
- Capped chat output tokens to the selected server profile and trimmed request history consistently across chat and prompt-fallback paths.
- Added regression coverage for request-history trimming and oversized latest-message clipping.

## [0.2.0] - 2026-06-21
- Added OpenRouter as a server provider, enabling cloud-hosted models alongside the local vLLM server.
- Added automatic tab switching when a panel run starts or completes.
- Refactored the vLLM server model into separate configuration, runtime state, calculations, and status models.
- Split the Models controller into Configuration, Selection, Actions, and Views partials and added a server-profile form mapper.
- Expanded server-profile validation and added the corresponding tests.
- Embedded the release version (derived from the git tag) into the published build.

## [0.1.0] - 2026-06-10
- Added a GitHub Actions release workflow to build, test, and publish release artifacts on tag push.
- Improved Mermaid troubleshooting, rendering normalization, and diagnostics stability across controllers and services.
- Added and updated tests for markdown rendering, request orchestration, conversation flow, theme handling, and Mermaid troubleshooting.
- Updated project documentation and development commands.

## [0.0.1] - 2026-05-26
- Initial implementation
