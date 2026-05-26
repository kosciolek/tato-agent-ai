package transcript

import (
	"encoding/json"
	"os"
	"sync"
	"time"
)

type Logger struct {
	mu   sync.Mutex
	file *os.File
}

type Entry struct {
	Time    time.Time              `json:"time"`
	Role    string                 `json:"role"`
	Content string                 `json:"content"`
	Meta    map[string]interface{} `json:"meta,omitempty"`
}

func Open(path string) (*Logger, error) {
	file, err := os.OpenFile(path, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0644)
	if err != nil {
		return nil, err
	}
	return &Logger{file: file}, nil
}

func (l *Logger) Write(role, content string, meta map[string]interface{}) error {
	if l == nil {
		return nil
	}
	l.mu.Lock()
	defer l.mu.Unlock()
	entry := Entry{
		Time:    time.Now().UTC(),
		Role:    role,
		Content: content,
		Meta:    meta,
	}
	data, err := json.Marshal(entry)
	if err != nil {
		return err
	}
	if _, err := l.file.Write(append(data, '\n')); err != nil {
		return err
	}
	return nil
}

func (l *Logger) Close() error {
	if l == nil || l.file == nil {
		return nil
	}
	return l.file.Close()
}
