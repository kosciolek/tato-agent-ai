package agent

import (
	"context"
	"encoding/json"
	"strings"
	"testing"

	"github.com/kosciolek/tato-agent-ai/internal/openai"
)

type fakeClient struct {
	requests []openai.CreateResponseRequest
	resps    []*openai.Response
}

func (f *fakeClient) CreateResponse(_ context.Context, req openai.CreateResponseRequest) (*openai.Response, error) {
	f.requests = append(f.requests, req)
	resp := f.resps[0]
	f.resps = f.resps[1:]
	return resp, nil
}

type fakeTools struct{}

func (fakeTools) Definitions() []openai.Tool { return nil }

func (fakeTools) Run(_ context.Context, name string, raw json.RawMessage) (string, error) {
	if name != "read_file" {
		return "", nil
	}
	return "file contents", nil
}

func TestAgentFeedsToolOutputBackToResponses(t *testing.T) {
	client := &fakeClient{resps: []*openai.Response{
		{
			ID: "resp_1",
			Output: []openai.OutputItem{
				{Type: "function_call", CallID: "call_1", Name: "read_file", Arguments: json.RawMessage(`{"path":"README.md"}`)},
			},
		},
		{
			ID: "resp_2",
			Output: []openai.OutputItem{
				{Type: "message", Content: []openai.ContentPart{{Type: "output_text", Text: "done"}}},
			},
		},
	}}
	a := New(Config{
		Model:        "test-model",
		Instructions: "test instructions",
		Client:       client,
		Tools:        fakeTools{},
	})

	out, err := a.Send(context.Background(), "inspect")
	if err != nil {
		t.Fatal(err)
	}
	if out != "done" {
		t.Fatalf("out = %q", out)
	}
	if len(client.requests) != 2 {
		t.Fatalf("requests = %d", len(client.requests))
	}
	if client.requests[0].Instructions == "" {
		t.Fatal("first request should include instructions")
	}
	if client.requests[1].PreviousResponseID != "resp_1" {
		t.Fatalf("previous id = %q", client.requests[1].PreviousResponseID)
	}
	items, ok := client.requests[1].Input.([]openai.FunctionCallOutput)
	if !ok || len(items) != 1 {
		t.Fatalf("unexpected second input %#v", client.requests[1].Input)
	}
	if items[0].CallID != "call_1" || !strings.Contains(items[0].Output, "file contents") {
		t.Fatalf("tool output = %#v", items[0])
	}
}
