# tato-agent-ai

Private minimal coding agent harness for local code work.

## Features

- OpenAI Responses API client with function tools.
- Interactive prompt loop.
- Local file listing, reading, text search, file writing, and shell command execution.
- Ask-before-write/execute by default.
- `--bypass-permissions` mode for unattended local side effects.
- OpenAI-hosted web search enabled by default.
- Optional JSONL transcript logging.
- No MCP support.

## Build

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
