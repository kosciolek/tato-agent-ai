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
	)
	flag.Parse()

	apiKey := os.Getenv("OPENAI_API_KEY")
	if apiKey == "" {
		fmt.Fprintln(os.Stderr, "OPENAI_API_KEY is not set")
		os.Exit(1)
	}

	absCWD, err := filepath.Abs(*cwd)
	if err != nil {
		fmt.Fprintf(os.Stderr, "resolve cwd: %v\n", err)
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
		Instructions: defaultInstructions(absCWD),
	})

	fmt.Printf("tato-agent-ai using %s in %s\n", *model, absCWD)
	if *bypassPermissions {
		fmt.Println("permission mode: bypass")
	} else {
		fmt.Println("permission mode: ask before write/command")
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
	return "You are a minimal local coding agent. Work in the configured directory: " + cwd + `. Use tools to inspect files before editing. Keep replies concise and factual. When you need local context, call list_files, read_file, or search_text. Use write_file for edits and run_command for checks or commands. No MCP tools are available.`
}
