package main

import (
	"bufio"
	"context"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"

	"github.com/kosciolek/tato-agent-ai/internal/agent"
	"github.com/kosciolek/tato-agent-ai/internal/openai"
	"github.com/kosciolek/tato-agent-ai/internal/tools"
	"github.com/kosciolek/tato-agent-ai/internal/transcript"
)

func main() {
	var (
		model             = flag.String("model", "gpt-5.5", "OpenAI model to use")
		cwd               = flag.String("cwd", ".", "working directory exposed to the agent")
		bypassPermissions = flag.Bool("bypass-permissions", false, "run write and command tools without asking")
		transcriptPath    = flag.String("transcript", "", "optional JSONL transcript path")
		webSearch         = flag.Bool("web-search", false, "enable OpenAI-hosted web search")
	)
	flag.Parse()

	absCWD, err := filepath.Abs(*cwd)
	if err != nil {
		fmt.Fprintf(os.Stderr, "resolve cwd: %v\n", err)
		os.Exit(1)
	}

	apiKey, err := loadAPIKey(absCWD)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}

	var log *transcript.Logger
	if *transcriptPath != "" {
		log, err = transcript.Open(*transcriptPath)
		if err != nil {
			fmt.Fprintf(os.Stderr, "open transcript: %v\n", err)
			os.Exit(1)
		}
		defer log.Close()
	}

	reader := bufio.NewReader(os.Stdin)
	runner := tools.NewRunner(absCWD, reader, os.Stdout, *bypassPermissions)
	client := openai.NewClient(apiKey)
	harness := agent.New(agent.Config{
		Model:        *model,
		WorkingDir:   absCWD,
		Client:       client,
		Tools:        runner,
		Transcript:   log,
		WebSearch:    *webSearch,
		Instructions: defaultInstructions(absCWD),
	})

	fmt.Printf("tato-agent-ai using %s in %s\n", *model, absCWD)
	if *bypassPermissions {
		fmt.Println("permission mode: bypass")
	} else {
		fmt.Println("permission mode: ask before write/command")
	}
	if *webSearch {
		fmt.Println("web search: enabled")
	}
	fmt.Println("type /exit to quit")

	for {
		fmt.Print("\nyou> ")
		line, err := reader.ReadString('\n')
		if err != nil && len(line) == 0 {
			if errors.Is(err, io.EOF) {
				break
			}
			fmt.Fprintf(os.Stderr, "read input: %v\n", err)
			os.Exit(1)
		}
		input := strings.TrimSpace(line)
		if input == "" {
			if err != nil {
				break
			}
			continue
		}
		if input == "/exit" || input == "/quit" {
			break
		}

		reply, err := harness.Send(context.Background(), input)
		if err != nil {
			fmt.Fprintf(os.Stderr, "agent error: %v\n", err)
			continue
		}
		if strings.TrimSpace(reply) != "" {
			fmt.Printf("\nagent> %s\n", reply)
		}
	}

}

func defaultInstructions(cwd string) string {
	return "You are a minimal local coding agent. Work in the configured directory: " + cwd + `. Use tools to inspect files before editing. Keep replies concise and factual. When you need local context, call list_files, read_file, or search_text. Use write_file for edits and run_command for checks or commands. Use hosted web search when it is enabled and the answer needs current internet information. No MCP tools are available.`
}

func loadAPIKey(cwd string) (string, error) {
	if key := strings.TrimSpace(os.Getenv("OPENAI_API_KEY")); key != "" {
		return key, nil
	}
	data, err := os.ReadFile(filepath.Join(cwd, ".openai-token"))
	if err == nil {
		if key := strings.TrimSpace(string(data)); key != "" {
			return key, nil
		}
	}
	if err != nil && !errors.Is(err, os.ErrNotExist) {
		return "", fmt.Errorf("read .openai-token: %w", err)
	}
	return "", fmt.Errorf("OPENAI_API_KEY is not set and %s is missing or empty", filepath.Join(cwd, ".openai-token"))
}
