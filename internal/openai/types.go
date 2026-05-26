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
	Name        string                 `json:"name,omitempty"`
	Description string                 `json:"description,omitempty"`
	Parameters  map[string]interface{} `json:"parameters,omitempty"`
	Strict      bool                   `json:"strict,omitempty"`
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
	Type        string       `json:"type"`
	Text        string       `json:"text,omitempty"`
	Annotations []Annotation `json:"annotations,omitempty"`
}

type Annotation struct {
	Type  string `json:"type"`
	URL   string `json:"url,omitempty"`
	Title string `json:"title,omitempty"`
}

type FunctionCallOutput struct {
	Type   string `json:"type"`
	CallID string `json:"call_id"`
	Output string `json:"output"`
}
