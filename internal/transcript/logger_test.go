package transcript

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestLoggerWritesJSONL(t *testing.T) {
	path := filepath.Join(t.TempDir(), "session.jsonl")
	log, err := Open(path)
	if err != nil {
		t.Fatal(err)
	}
	if err := log.Write("user", "hello", nil); err != nil {
		t.Fatal(err)
	}
	if err := log.Close(); err != nil {
		t.Fatal(err)
	}
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	text := string(data)
	if !strings.Contains(text, `"role":"user"`) || !strings.Contains(text, `"content":"hello"`) {
		t.Fatalf("unexpected log: %s", text)
	}
}
