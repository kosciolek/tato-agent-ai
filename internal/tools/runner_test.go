package tools

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestReadFileDecodesStringArguments(t *testing.T) {
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "hello.txt"), []byte("hello"), 0644); err != nil {
		t.Fatal(err)
	}
	r := NewRunner(dir, strings.NewReader(""), &strings.Builder{}, false)

	raw, _ := json.Marshal(`{"path":"hello.txt"}`)
	out, err := r.Run(context.Background(), "read_file", raw)
	if err != nil {
		t.Fatal(err)
	}
	if out != "hello" {
		t.Fatalf("out = %q", out)
	}
}

func TestWriteFileAsksAndDeniesByDefault(t *testing.T) {
	dir := t.TempDir()
	var prompt strings.Builder
	r := NewRunner(dir, strings.NewReader("n\n"), &prompt, false)

	raw := json.RawMessage(`{"path":"x.txt","content":"nope"}`)
	out, err := r.Run(context.Background(), "write_file", raw)
	if err != nil {
		t.Fatal(err)
	}
	if out != "denied by user" {
		t.Fatalf("out = %q", out)
	}
	if _, err := os.Stat(filepath.Join(dir, "x.txt")); !os.IsNotExist(err) {
		t.Fatalf("expected file not to exist, stat err %v", err)
	}
	if !strings.Contains(prompt.String(), "allow write file") {
		t.Fatalf("missing prompt: %q", prompt.String())
	}
}

func TestWriteFileBypass(t *testing.T) {
	dir := t.TempDir()
	r := NewRunner(dir, strings.NewReader(""), &strings.Builder{}, true)

	raw := json.RawMessage(`{"path":"sub/x.txt","content":"ok"}`)
	if _, err := r.Run(context.Background(), "write_file", raw); err != nil {
		t.Fatal(err)
	}
	data, err := os.ReadFile(filepath.Join(dir, "sub", "x.txt"))
	if err != nil {
		t.Fatal(err)
	}
	if string(data) != "ok" {
		t.Fatalf("content = %q", string(data))
	}
}

func TestPathEscapeRejected(t *testing.T) {
	dir := t.TempDir()
	r := NewRunner(dir, strings.NewReader(""), &strings.Builder{}, true)

	_, err := r.Run(context.Background(), "read_file", json.RawMessage(`{"path":"../x.txt"}`))
	if err == nil || !strings.Contains(err.Error(), "escapes") {
		t.Fatalf("expected escape error, got %v", err)
	}
}

func TestSearchText(t *testing.T) {
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "a.go"), []byte("package main\nfunc main() {}\n"), 0644); err != nil {
		t.Fatal(err)
	}
	r := NewRunner(dir, strings.NewReader(""), &strings.Builder{}, false)

	out, err := r.Run(context.Background(), "search_text", json.RawMessage(`{"pattern":"func"}`))
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out, "a.go:2:func main() {}") {
		t.Fatalf("unexpected search output: %q", out)
	}
}
