package tools

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"runtime"
	"sort"
	"strings"
	"time"

	"github.com/kosciolek/tato-agent-ai/internal/openai"
)

const (
	defaultReadLimit    = 256 * 1024
	defaultSearchLimit  = 100
	defaultCommandLimit = 120
	maxCommandLimit     = 600
)

type Runner struct {
	root   string
	in     io.Reader
	out    io.Writer
	bypass bool
}

func NewRunner(root string, in io.Reader, out io.Writer, bypass bool) *Runner {
	return &Runner{root: filepath.Clean(root), in: in, out: out, bypass: bypass}
}

func (r *Runner) Definitions() []openai.Tool {
	return []openai.Tool{
		functionTool("list_files", "List files and directories under the working directory.", objectSchema(map[string]interface{}{
			"path":        stringProp("Directory path relative to the working directory."),
			"max_entries": numberProp("Maximum number of entries to return."),
		}, []string{})),
		functionTool("read_file", "Read a text file under the working directory.", objectSchema(map[string]interface{}{
			"path":      stringProp("File path relative to the working directory."),
			"max_bytes": numberProp("Maximum bytes to read."),
		}, []string{"path"})),
		functionTool("search_text", "Search text files recursively under the working directory.", objectSchema(map[string]interface{}{
			"pattern":     stringProp("Substring or regular expression to search for."),
			"path":        stringProp("Directory path relative to the working directory."),
			"regex":       boolProp("Treat pattern as a regular expression."),
			"max_results": numberProp("Maximum number of matches to return."),
		}, []string{"pattern"})),
		functionTool("write_file", "Create or overwrite a text file under the working directory.", objectSchema(map[string]interface{}{
			"path":    stringProp("File path relative to the working directory."),
			"content": stringProp("Full file content to write."),
		}, []string{"path", "content"})),
		functionTool("run_command", "Run a shell command in the working directory and return combined output.", objectSchema(map[string]interface{}{
			"command":         stringProp("Command line to execute."),
			"timeout_seconds": numberProp("Timeout in seconds."),
		}, []string{"command"})),
	}
}

func (r *Runner) Run(ctx context.Context, name string, raw json.RawMessage) (string, error) {
	switch name {
	case "list_files":
		var args listFilesArgs
		if err := decodeArgs(raw, &args); err != nil {
			return "", err
		}
		return r.listFiles(args)
	case "read_file":
		var args readFileArgs
		if err := decodeArgs(raw, &args); err != nil {
			return "", err
		}
		return r.readFile(args)
	case "search_text":
		var args searchTextArgs
		if err := decodeArgs(raw, &args); err != nil {
			return "", err
		}
		return r.searchText(args)
	case "write_file":
		var args writeFileArgs
		if err := decodeArgs(raw, &args); err != nil {
			return "", err
		}
		return r.writeFile(args)
	case "run_command":
		var args runCommandArgs
		if err := decodeArgs(raw, &args); err != nil {
			return "", err
		}
		return r.runCommand(ctx, args)
	default:
		return "", fmt.Errorf("unknown tool %q", name)
	}
}

func decodeArgs(raw json.RawMessage, dest interface{}) error {
	if len(raw) == 0 {
		return json.Unmarshal([]byte("{}"), dest)
	}
	if raw[0] == '"' {
		var encoded string
		if err := json.Unmarshal(raw, &encoded); err != nil {
			return err
		}
		return json.Unmarshal([]byte(encoded), dest)
	}
	return json.Unmarshal(raw, dest)
}

type listFilesArgs struct {
	Path       string `json:"path"`
	MaxEntries int    `json:"max_entries"`
}

type readFileArgs struct {
	Path     string `json:"path"`
	MaxBytes int    `json:"max_bytes"`
}

type searchTextArgs struct {
	Pattern    string `json:"pattern"`
	Path       string `json:"path"`
	Regex      bool   `json:"regex"`
	MaxResults int    `json:"max_results"`
}

type writeFileArgs struct {
	Path    string `json:"path"`
	Content string `json:"content"`
}

type runCommandArgs struct {
	Command        string `json:"command"`
	TimeoutSeconds int    `json:"timeout_seconds"`
}

func (r *Runner) listFiles(args listFilesArgs) (string, error) {
	dir, err := r.resolve(args.Path)
	if err != nil {
		return "", err
	}
	entries, err := os.ReadDir(dir)
	if err != nil {
		return "", err
	}
	sort.Slice(entries, func(i, j int) bool { return entries[i].Name() < entries[j].Name() })
	limit := args.MaxEntries
	if limit <= 0 || limit > 1000 {
		limit = 200
	}
	var b strings.Builder
	for i, entry := range entries {
		if i >= limit {
			fmt.Fprintf(&b, "... truncated after %d entries\n", limit)
			break
		}
		suffix := ""
		if entry.IsDir() {
			suffix = "/"
		}
		fmt.Fprintf(&b, "%s%s\n", entry.Name(), suffix)
	}
	return b.String(), nil
}

func (r *Runner) readFile(args readFileArgs) (string, error) {
	if args.Path == "" {
		return "", errors.New("path is required")
	}
	path, err := r.resolve(args.Path)
	if err != nil {
		return "", err
	}
	limit := args.MaxBytes
	if limit <= 0 || limit > defaultReadLimit {
		limit = defaultReadLimit
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return "", err
	}
	if len(data) > limit {
		return string(data[:limit]) + fmt.Sprintf("\n... truncated at %d bytes", limit), nil
	}
	return string(data), nil
}

func (r *Runner) searchText(args searchTextArgs) (string, error) {
	if args.Pattern == "" {
		return "", errors.New("pattern is required")
	}
	base, err := r.resolve(args.Path)
	if err != nil {
		return "", err
	}
	limit := args.MaxResults
	if limit <= 0 || limit > 1000 {
		limit = defaultSearchLimit
	}
	var re *regexp.Regexp
	if args.Regex {
		re, err = regexp.Compile(args.Pattern)
		if err != nil {
			return "", err
		}
	}

	var results []string
	err = filepath.WalkDir(base, func(path string, d os.DirEntry, walkErr error) error {
		if walkErr != nil {
			return nil
		}
		if len(results) >= limit {
			return filepath.SkipAll
		}
		if d.IsDir() {
			switch d.Name() {
			case ".git", "node_modules", "vendor", "target", "dist", "build":
				if path != base {
					return filepath.SkipDir
				}
			}
			return nil
		}
		if !isTextLike(path) {
			return nil
		}
		data, err := os.ReadFile(path)
		if err != nil || bytesLookBinary(data) {
			return nil
		}
		rel, _ := filepath.Rel(r.root, path)
		lines := strings.Split(string(data), "\n")
		for i, line := range lines {
			matched := false
			if re != nil {
				matched = re.MatchString(line)
			} else {
				matched = strings.Contains(line, args.Pattern)
			}
			if matched {
				results = append(results, fmt.Sprintf("%s:%d:%s", rel, i+1, line))
				if len(results) >= limit {
					return filepath.SkipAll
				}
			}
		}
		return nil
	})
	if err != nil {
		return "", err
	}
	if len(results) == 0 {
		return "no matches", nil
	}
	return strings.Join(results, "\n"), nil
}

func (r *Runner) writeFile(args writeFileArgs) (string, error) {
	if args.Path == "" {
		return "", errors.New("path is required")
	}
	path, err := r.resolve(args.Path)
	if err != nil {
		return "", err
	}
	if !r.confirm("write file " + path) {
		return "denied by user", nil
	}
	if err := os.MkdirAll(filepath.Dir(path), 0755); err != nil {
		return "", err
	}
	if err := os.WriteFile(path, []byte(args.Content), 0644); err != nil {
		return "", err
	}
	return fmt.Sprintf("wrote %d bytes to %s", len(args.Content), path), nil
}

func (r *Runner) runCommand(ctx context.Context, args runCommandArgs) (string, error) {
	if strings.TrimSpace(args.Command) == "" {
		return "", errors.New("command is required")
	}
	if !r.confirm("run command: " + args.Command) {
		return "denied by user", nil
	}
	timeout := args.TimeoutSeconds
	if timeout <= 0 {
		timeout = defaultCommandLimit
	}
	if timeout > maxCommandLimit {
		timeout = maxCommandLimit
	}
	cmdCtx, cancel := context.WithTimeout(ctx, time.Duration(timeout)*time.Second)
	defer cancel()

	var cmd *exec.Cmd
	if runtime.GOOS == "windows" {
		cmd = exec.CommandContext(cmdCtx, "cmd.exe", "/C", args.Command)
	} else {
		cmd = exec.CommandContext(cmdCtx, "/bin/sh", "-lc", args.Command)
	}
	cmd.Dir = r.root
	out, err := cmd.CombinedOutput()
	text := limitOutput(string(out))
	if cmdCtx.Err() == context.DeadlineExceeded {
		return text + "\nERROR: command timed out", nil
	}
	if err != nil {
		return text + "\nERROR: " + err.Error(), nil
	}
	return text, nil
}

func (r *Runner) resolve(p string) (string, error) {
	if p == "" {
		p = "."
	}
	candidate := p
	if !filepath.IsAbs(candidate) {
		candidate = filepath.Join(r.root, candidate)
	}
	candidate = filepath.Clean(candidate)
	rel, err := filepath.Rel(r.root, candidate)
	if err != nil {
		return "", err
	}
	if rel == ".." || strings.HasPrefix(rel, ".."+string(os.PathSeparator)) {
		return "", fmt.Errorf("path escapes working directory: %s", p)
	}
	return candidate, nil
}

func (r *Runner) confirm(action string) bool {
	if r.bypass {
		return true
	}
	fmt.Fprintf(r.out, "\nallow %s? [y/N] ", action)
	answer := ""
	if reader, ok := r.in.(*bufio.Reader); ok {
		answer, _ = reader.ReadString('\n')
	} else {
		reader := bufio.NewReader(r.in)
		answer, _ = reader.ReadString('\n')
	}
	answer = strings.TrimSpace(strings.ToLower(answer))
	return answer == "y" || answer == "yes"
}

func functionTool(name, description string, parameters map[string]interface{}) openai.Tool {
	return openai.Tool{
		Type:        "function",
		Name:        name,
		Description: description,
		Parameters:  parameters,
		Strict:      false,
	}
}

func objectSchema(props map[string]interface{}, required []string) map[string]interface{} {
	return map[string]interface{}{
		"type":                 "object",
		"properties":           props,
		"required":             required,
		"additionalProperties": false,
	}
}

func stringProp(description string) map[string]interface{} {
	return map[string]interface{}{"type": "string", "description": description}
}

func numberProp(description string) map[string]interface{} {
	return map[string]interface{}{"type": "integer", "description": description}
}

func boolProp(description string) map[string]interface{} {
	return map[string]interface{}{"type": "boolean", "description": description}
}

func bytesLookBinary(data []byte) bool {
	if len(data) > 8192 {
		data = data[:8192]
	}
	for _, b := range data {
		if b == 0 {
			return true
		}
	}
	return false
}

func isTextLike(path string) bool {
	switch strings.ToLower(filepath.Ext(path)) {
	case ".exe", ".dll", ".png", ".jpg", ".jpeg", ".gif", ".zip", ".tar", ".gz", ".7z", ".pdf", ".ico":
		return false
	default:
		return true
	}
}

func limitOutput(s string) string {
	const max = 256 * 1024
	if len(s) <= max {
		return s
	}
	return s[:max] + fmt.Sprintf("\n... truncated at %d bytes", max)
}
