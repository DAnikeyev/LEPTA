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

## Notes

- The test project contains integration tests in `LEPTA.Tests/UnitTest1.cs` that are tagged with `Category("Integration")`.
- The standard non-integration command above is the safest default when you do not have a live vLLM server running.
- If a build fails because an output DLL is locked, check whether a long-running sandbox or benchmark tool is still holding files from a previous run.
- The current project files target `net10.0` and `net10.0-windows`, so use a matching SDK.

