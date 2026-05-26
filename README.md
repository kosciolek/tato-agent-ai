# tato-agent-ai

Private minimal coding agent harness for local code work.

## Apps

- `agent-full`: the existing Go command-line agent with local reads, writes, shell command execution, and OpenAI-hosted web search.
- `agent-readonly`: a Windows desktop UI for asking questions about a local codebase. It can list, search, and read files, and it can use OpenAI-hosted web search. It cannot write files, run commands, or use MCP.

## agent-full features

- OpenAI Responses API client with function tools.
- Interactive prompt loop.
- Local file listing, reading, text search, file writing, and shell command execution.
- Ask-before-write/execute by default.
- `--bypass-permissions` mode for unattended local side effects.
- OpenAI-hosted web search enabled by default.
- Optional JSONL transcript logging.
- No MCP support.

## Build agent-full

```sh
go build ./cmd/tato-agent-ai
```

Windows 7 compatibility depends on building with Go 1.20.x. Go 1.21 and newer do not target Windows 7.

## Run

```sh
export OPENAI_API_KEY=...
./tato-agent-ai --cwd /path/to/project
```

If `OPENAI_API_KEY` is not set, the harness reads an API token from `.openai-token`
in the selected `--cwd` directory. The file should contain only the token.

Useful flags:

- `--model gpt-5.5`
- `--cwd .`
- `--bypass-permissions`
- `--transcript session.jsonl`

Inside the prompt, type `/exit` or `/quit` to stop.

## Build agent-readonly

`agent-readonly` is a .NET Framework 4.8 WPF app intended to run on Windows 7 SP1 and newer Windows versions that have .NET Framework 4.8 installed.

```powershell
.\agent-readonly\build.ps1
```

Runtime files next to `agent-readonly.exe`:

- `.openai-api-key`: contains only the OpenAI API key.
- `CONTEXT.md`: optional project guidance loaded into the agent instructions.

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
