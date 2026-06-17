# LEPTA development commands

This file keeps the normal local commands small and explicit.

## Restore

```powershell
cd C:\Repos\LEPTA
dotnet restore LEPTA.sln
```

## Normal test pass

Use this for routine validation without the live-server integration tests.

```powershell
cd C:\Repos\LEPTA
dotnet test LEPTA.Tests\LEPTA.Tests.csproj --filter "Category!=Integration"
```

## Build

```powershell
cd C:\Repos\LEPTA
dotnet build LEPTA.sln
```

## Publish

Produces a single self-contained `.exe` for Windows x64.

```powershell
cd C:\Repos\LEPTA
dotnet publish LEPTA\LEPTA.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish
```

The output will be in `publish\LEPTA.exe`.

## Notes

- The test project contains integration tests (in `LEPTA.Tests/VllmCompletionIntegrationTests.cs` and `LEPTA.Tests/VllmMermaidTroubleshootIntegrationTests.cs`) that are tagged with `Category("Integration")` and require a live OpenAI-compatible server at `http://localhost:8512` (override with the `VLLM_BASE_URL` env var).
- To run those integration tests explicitly:
  ```
  dotnet test LEPTA.Tests\LEPTA.Tests.csproj --filter "Category=Integration"
  ```
- The standard non-integration command above is the safest default when you do not have a live vLLM server running.
- If a build fails because an output DLL is locked, check whether a long-running sandbox or benchmark tool is still holding files from a previous run.
- The current project files target `net10.0` and `net10.0-windows`, so use a matching SDK.

