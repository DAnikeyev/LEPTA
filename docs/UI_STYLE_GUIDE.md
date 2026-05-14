# LEPTA UI style guide

Use this guide for future UI changes so light and dark themes stay readable.

## Core rules
- Never hardcode control colors in windows or controllers. Use `DynamicResource` with keys from `LEPTA/Theming/ThemeResourceKeys.cs`.
- New cards should use `CardBorderStyle`.
- Primary actions use the default `Button` style. Secondary actions use `SecondaryButtonStyle`.
- Navigation items should match `NavigationButton`.
- For chat or status surfaces, prefer `PanelBackgroundAltBrush`, `MessageSurfaceBrush`, `PrimaryTextBrush`, and `SecondaryTextBrush` instead of raw WPF defaults.

## Theme safety
- `LEPTA/Controllers/ThemeController.cs` maps WPF system brushes to LEPTA theme brushes. Keep using those mappings when adding controls with built-in popups or selections.
- If a new control shows unreadable text in either theme, fix it through theme resources or system brush mappings instead of one-off colors.
- Test both dark and light theme after UI edits, especially:
  - selected items
  - combo box dropdowns
  - text selection
  - read-only text areas
  - overlays and dialogs

## Chat UI rule
- Current chat support is intentionally limited to already deployed HTTP vLLM servers.
- If future work enables Docker-managed local chat, keep the HTTP path working and clearly label unsupported modes in the UI.

