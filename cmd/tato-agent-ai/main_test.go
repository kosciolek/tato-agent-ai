package main

import (
	"os"
	"path/filepath"
	"testing"
)

func TestLoadAPIKeyPrefersEnvironment(t *testing.T) {
	t.Setenv("OPENAI_API_KEY", " env-key \n")
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, ".openai-token"), []byte("file-key"), 0600); err != nil {
		t.Fatal(err)
	}

	key, err := loadAPIKey(dir)
	if err != nil {
		t.Fatal(err)
	}
	if key != "env-key" {
		t.Fatalf("key = %q", key)
	}
}

func TestLoadAPIKeyFromTokenFile(t *testing.T) {
	t.Setenv("OPENAI_API_KEY", "")
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, ".openai-token"), []byte(" file-key \n"), 0600); err != nil {
		t.Fatal(err)
	}

	key, err := loadAPIKey(dir)
	if err != nil {
		t.Fatal(err)
	}
	if key != "file-key" {
		t.Fatalf("key = %q", key)
	}
}

func TestLoadAPIKeyMissing(t *testing.T) {
	t.Setenv("OPENAI_API_KEY", "")
	_, err := loadAPIKey(t.TempDir())
	if err == nil {
		t.Fatal("expected error")
	}
}
