# tato-agent-ai

Windows desktop app for asking read-only questions about a local codebase.

The app can list, search, and read files in the selected folder, and can use OpenAI-hosted web search when current external information is needed. It cannot write files, run commands, edit code, or use MCP.

## Build

The app is a .NET Framework 4.8 WPF project intended for Windows x64 machines with .NET Framework 4.8 installed.

```powershell
.\build.ps1
```

The default build target is `x64`. The output is written to:

```text
bin\x64\Release
```

Runtime files next to `agent-readonly.exe`:

- `.openai-api-key`: contains only the OpenAI API key.
- `CONTEXT.md`: optional project guidance loaded into the agent instructions.

## Updates

The app checks the GitHub `latest` release in the background on launch and every 10 minutes. If a newer `agent-readonly-windows-x64.zip` is available, an `Update` button appears in the top bar.

Releases publish:

- `agent-readonly-windows-x64.zip`
- `agent-readonly-windows-x64.manifest.json`

## Context

Recommended `CONTEXT.md` structure:

```md
# Context

## Purpose
What this codebase does and who uses it.

## Architecture
Main apps/services, important directories, and how data flows.

## Current Priorities
What you are currently trying to understand or change.

## Conventions
Naming, style, testing expectations, and important design constraints.

## Commands
How to build, test, run, and inspect the project.

## Ignore
Directories/files the agent should avoid unless explicitly requested.

## Glossary
Domain terms, abbreviations, and project-specific vocabulary.
```
