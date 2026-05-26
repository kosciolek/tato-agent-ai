package agent

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"

	"github.com/kosciolek/tato-agent-ai/internal/openai"
	"github.com/kosciolek/tato-agent-ai/internal/tools"
	"github.com/kosciolek/tato-agent-ai/internal/transcript"
)

const maxToolRounds = 16

type ResponseClient interface {
	CreateResponse(context.Context, openai.CreateResponseRequest) (*openai.Response, error)
}

type ToolRunner interface {
	Definitions() []openai.Tool
	Run(context.Context, string, json.RawMessage) (string, error)
}

type Config struct {
	Model        string
	WorkingDir   string
	Instructions string
	Client       ResponseClient
	Tools        ToolRunner
	Transcript   *transcript.Logger
}

type Agent struct {
	cfg                Config
	previousResponseID string
}

func New(cfg Config) *Agent {
	return &Agent{cfg: cfg}
}

func (a *Agent) Send(ctx context.Context, userInput string) (string, error) {
	if a.cfg.Transcript != nil {
		_ = a.cfg.Transcript.Write("user", userInput, nil)
	}

	var input interface{} = userInput
	var final strings.Builder

	for round := 0; round < maxToolRounds; round++ {
		req := openai.CreateResponseRequest{
			Model:              a.cfg.Model,
			Input:              input,
			Tools:              a.cfg.Tools.Definitions(),
			ToolChoice:         "auto",
			PreviousResponseID: a.previousResponseID,
		}
		if a.previousResponseID == "" {
			req.Instructions = a.cfg.Instructions
		}

		resp, err := a.cfg.Client.CreateResponse(ctx, req)
		if err != nil {
			return "", err
		}
		a.previousResponseID = resp.ID

		var toolOutputs []openai.FunctionCallOutput
		for _, item := range resp.Output {
			switch item.Type {
			case "message":
				for _, part := range item.Content {
					if part.Type == "output_text" && part.Text != "" {
						if final.Len() > 0 {
							final.WriteString("\n")
						}
						final.WriteString(part.Text)
					}
				}
			case "function_call":
				out, err := a.cfg.Tools.Run(ctx, item.Name, item.Arguments)
				if err != nil {
					out = "ERROR: " + err.Error()
				}
				toolOutputs = append(toolOutputs, openai.FunctionCallOutput{
					Type:   "function_call_output",
					CallID: item.CallID,
					Output: out,
				})
				if a.cfg.Transcript != nil {
					meta := map[string]interface{}{
						"tool":      item.Name,
						"call_id":   item.CallID,
						"arguments": json.RawMessage(item.Arguments),
					}
					_ = a.cfg.Transcript.Write("tool", out, meta)
				}
			}
		}

		if len(toolOutputs) == 0 {
			text := final.String()
			if a.cfg.Transcript != nil && strings.TrimSpace(text) != "" {
				_ = a.cfg.Transcript.Write("assistant", text, nil)
			}
			return text, nil
		}
		input = toolOutputs
	}

	return final.String(), fmt.Errorf("stopped after %d tool rounds", maxToolRounds)
}

var _ ToolRunner = (*tools.Runner)(nil)
