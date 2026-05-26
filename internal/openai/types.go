package openai

import "encoding/json"

type CreateResponseRequest struct {
	Model              string      `json:"model"`
	Instructions       string      `json:"instructions,omitempty"`
	Input              interface{} `json:"input"`
	Tools              []Tool      `json:"tools,omitempty"`
	ToolChoice         string      `json:"tool_choice,omitempty"`
	PreviousResponseID string      `json:"previous_response_id,omitempty"`
}

type Tool struct {
	Type        string                 `json:"type"`
	Name        string                 `json:"name"`
	Description string                 `json:"description"`
	Parameters  map[string]interface{} `json:"parameters"`
	Strict      bool                   `json:"strict"`
}

type Response struct {
	ID     string       `json:"id"`
	Status string       `json:"status"`
	Output []OutputItem `json:"output"`
}

type OutputItem struct {
	Type      string          `json:"type"`
	Role      string          `json:"role,omitempty"`
	Content   []ContentPart   `json:"content,omitempty"`
	CallID    string          `json:"call_id,omitempty"`
	Name      string          `json:"name,omitempty"`
	Arguments json.RawMessage `json:"arguments,omitempty"`
}

type ContentPart struct {
	Type string `json:"type"`
	Text string `json:"text,omitempty"`
}

type FunctionCallOutput struct {
	Type   string `json:"type"`
	CallID string `json:"call_id"`
	Output string `json:"output"`
}
