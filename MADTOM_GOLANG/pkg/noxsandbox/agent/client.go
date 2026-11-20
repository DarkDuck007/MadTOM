package agent

import (
	"bytes"
	"context"
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"time"
	"unicode/utf8"

	"github.com/DarkDuck007/madtom/pkg/noxsandbox/vmm"
)

type Client struct {
	Machine       vmm.Machine
	MaxFrameBytes int
}

type envelope struct {
	ID    int     `json:"id"`
	OK    *bool   `json:"ok"`
	Error *string `json:"error"`
}

type ProtocolError struct{ Reason string }

func (err *ProtocolError) Error() string { return "invalid guest response: " + err.Reason }

type RemoteError struct{ Message string }

func (err *RemoteError) Error() string { return err.Message }

func exchange(ctx context.Context, client Client, op string, fields map[string]any, response any) error {
	if fields == nil {
		fields = make(map[string]any)
	}
	fields["id"], fields["op"] = 1, op
	request, err := json.Marshal(fields)
	if err != nil {
		return err
	}
	if len(request) > client.MaxFrameBytes || uint64(len(request)) > uint64(^uint32(0)) {
		return fmt.Errorf("guest request exceeds frame limit")
	}
	if _, ok := ctx.Deadline(); !ok {
		var cancel context.CancelFunc
		ctx, cancel = context.WithTimeout(ctx, 30*time.Second)
		defer cancel()
	}
	conn, err := client.Machine.Dial(ctx)
	if err != nil {
		return err
	}
	defer conn.Close()
	deadline, _ := ctx.Deadline()
	if err := conn.SetDeadline(deadline); err != nil {
		return err
	}
	stop := context.AfterFunc(ctx, func() { _ = conn.Close() })
	defer stop()
	finished := make(chan struct{})
	defer close(finished)
	go func() {
		select {
		case <-client.Machine.Done():
			_ = conn.Close()
		case <-finished:
		}
	}()
	var header [4]byte
	binary.BigEndian.PutUint32(header[:], uint32(len(request)))
	if _, err := io.Copy(conn, io.MultiReader(bytes.NewReader(header[:]), bytes.NewReader(request))); err != nil {
		return err
	}
	if _, err := io.ReadFull(conn, header[:]); err != nil {
		return err
	}
	length := binary.BigEndian.Uint32(header[:])
	if length == 0 || int64(length) > min(int64(client.MaxFrameBytes), responseLimit(op, fields)) {
		return &ProtocolError{"invalid frame size"}
	}
	data := make([]byte, int(length))
	if _, err := io.ReadFull(conn, data); err != nil {
		if errors.Is(err, io.EOF) || errors.Is(err, io.ErrUnexpectedEOF) {
			return &ProtocolError{"incomplete frame"}
		}
		return err
	}
	if !utf8.Valid(data) {
		return &ProtocolError{"invalid UTF-8"}
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(response); err != nil {
		return &ProtocolError{"invalid JSON schema"}
	}
	if err := decoder.Decode(new(any)); err != io.EOF {
		return &ProtocolError{"trailing JSON"}
	}
	return nil
}

func responseLimit(op string, fields map[string]any) int64 {
	const overhead = 16 << 10
	bounded := func(key string) int64 {
		value, _ := fields[key].(int)
		return int64(min(max(value, 0), 1<<30))
	}
	switch op {
	case "exec":
		return overhead + 18*bounded("max_output_bytes")
	case "put_files":
		files, _ := fields["files"].([]Input)
		return overhead + 4096*int64(len(files))
	case "get_files":
		return overhead + 4*((bounded("max_total_bytes")+2)/3) + 8192*min(bounded("max_files"), 2048)
	case "list", "manifest":
		return overhead + 8192*bounded("max_entries")
	case "read_text":
		return overhead + 6*bounded("max_bytes")
	case "render":
		pixels := min(bounded("max_px"), 32768)
		return overhead + 4*((4*pixels*pixels+2)/3)
	case "pre_run", "sessions":
		return 2 << 20
	default:
		return overhead
	}
}

func validate(reply envelope, allowGuestFailure bool) error {
	if reply.ID != 1 || reply.OK == nil {
		return &ProtocolError{"missing or invalid id/ok"}
	}
	if !*reply.OK && !allowGuestFailure {
		if reply.Error != nil {
			return &RemoteError{(*reply.Error)[:min(len(*reply.Error), 1024)]}
		}
		return &RemoteError{"guest operation failed"}
	}
	return nil
}

func simple(ctx context.Context, client Client, op string, fields map[string]any) error {
	var response envelope
	if err := exchange(ctx, client, op, fields, &response); err != nil {
		return err
	}
	return validate(response, false)
}

type Hello struct {
	AgentVersion string `json:"agent_version"`
	ImageVersion string `json:"image_version"`
	BootID       string `json:"boot_id"`
}

func (client Client) Hello(ctx context.Context) (Hello, error) {
	var response struct {
		envelope
		Hello
	}
	if err := exchange(ctx, client, "hello", nil, &response); err != nil {
		return Hello{}, err
	}
	return response.Hello, validate(response.envelope, false)
}

func (client Client) Init(ctx context.Context, now time.Time, entropy []byte, role vmm.Role, pids, output int) error {
	if len(entropy) != 64 {
		return fmt.Errorf("expected 64 bytes of entropy")
	}
	return simple(ctx, client, "init", map[string]any{"time_unix_ns": now.UnixNano(), "entropy_b64": entropy, "role": role, "limits": map[string]int{"pids_max": pids, "max_output_bytes": output}})
}

type ExecResult struct {
	ExitCode     int     `json:"exit_code"`
	Stdout       string  `json:"stdout"`
	Stderr       string  `json:"stderr"`
	Truncated    bool    `json:"truncated"`
	ResultRepr   *string `json:"result_repr"`
	SessionReset bool    `json:"session_reset"`
	DurationMS   int64   `json:"duration_ms"`
	CPUMS        int64   `json:"cpu_ms"`
	Session      string  `json:"session"` // echoed by the guest agent; informational only
	OK           bool    `json:"-"`
	Error        *string `json:"-"`
}

func (client Client) Exec(ctx context.Context, session, kind, code string, timeout, output int) (ExecResult, error) {
	var response struct {
		envelope
		ExecResult
	}
	fields := map[string]any{"session": session, "kind": kind, "code": code, "timeout_s": timeout, "max_output_bytes": output}
	if err := exchange(ctx, client, "exec", fields, &response); err != nil {
		return ExecResult{}, err
	}
	if err := validate(response.envelope, true); err != nil {
		return ExecResult{}, err
	}
	result := response.ExecResult
	result.OK, result.Error = *response.envelope.OK, response.envelope.Error
	result.Stdout, result.Truncated = limit(result.Stdout, output, result.Truncated)
	result.Stderr, result.Truncated = limit(result.Stderr, output-len(result.Stdout), result.Truncated)
	for _, field := range []*string{result.ResultRepr, result.Error} {
		if field != nil {
			*field, result.Truncated = limit(*field, output, result.Truncated)
		}
	}
	return result, nil
}

func limit(value string, size int, truncated bool) (string, bool) {
	if len(value) <= size {
		return value, truncated
	}
	size = max(0, size)
	for size > 0 && !utf8.RuneStart(value[size]) {
		size--
	}
	return value[:size], true
}

type Input struct {
	Name string `json:"name"`
	Data []byte `json:"data_b64"`
}
type File struct {
	Name string `json:"name"`
	Path string `json:"path"`
	Size int64  `json:"size"`
	Data []byte `json:"data_b64"`
	// Sanitized is set only by the host (manager.cleanFiles); any guest-supplied value is overwritten.
	Sanitized bool `json:"sanitized"`
}
type Skipped struct {
	Path   string `json:"path"`
	Reason string `json:"reason"`
}
type Files struct {
	Files   []File    `json:"files"`
	Skipped []Skipped `json:"skipped"`
}

func (client Client) PutFiles(ctx context.Context, dir string, files []Input) ([]string, error) {
	var response struct {
		envelope
		Written []string `json:"written"`
	}
	if err := exchange(ctx, client, "put_files", map[string]any{"dir": dir, "files": files}, &response); err != nil {
		return nil, err
	}
	if len(response.Written) > len(files) {
		return nil, &ProtocolError{"too many put_files results"}
	}
	return response.Written, validate(response.envelope, false)
}

func (client Client) GetFiles(ctx context.Context, paths []string, maxBytes, maxFiles int) (Files, error) {
	var response struct {
		envelope
		Files
	}
	err := exchange(ctx, client, "get_files", map[string]any{"paths": paths, "max_total_bytes": maxBytes, "max_files": maxFiles}, &response)
	if err != nil {
		return Files{}, err
	}
	return response.Files, validate(response.envelope, false)
}

type Entry struct {
	Path  string          `json:"path"`
	Type  string          `json:"type"`
	Size  int64           `json:"size"`
	MTime json.RawMessage `json:"mtime"`
}
type Manifest struct {
	Entries    []Entry `json:"entries"`
	UsedBytes  int64   `json:"used_bytes"`
	FreeBytes  int64   `json:"free_bytes"`
	TotalBytes int64   `json:"total_bytes"`
}

func entries(input []Entry, count int) []Entry {
	result := make([]Entry, 0, min(len(input), count))
	for _, entry := range input[:min(len(input), count)] {
		switch entry.Type {
		case "file", "dir", "symlink", "other":
		default:
			continue
		}
		if len(entry.Path) > 4096 || entry.Size < 0 || len(entry.MTime) > 128 {
			continue
		}
		if len(entry.MTime) > 0 && (entry.MTime[0] == '{' || entry.MTime[0] == '[') {
			continue
		}
		result = append(result, entry)
	}
	return result
}

func (client Client) List(ctx context.Context, path string, depth, count int) ([]Entry, bool, error) {
	var response struct {
		envelope
		Entries   []Entry `json:"entries"`
		Truncated bool    `json:"truncated"`
	}
	err := exchange(ctx, client, "list", map[string]any{"path": path, "depth": depth, "max_entries": count}, &response)
	if err != nil {
		return nil, false, err
	}
	return entries(response.Entries, count), response.Truncated || len(response.Entries) > count, validate(response.envelope, false)
}

func (client Client) Manifest(ctx context.Context, count int) (Manifest, error) {
	var response struct {
		envelope
		Manifest
	}
	if err := exchange(ctx, client, "manifest", map[string]any{"max_entries": count}, &response); err != nil {
		return Manifest{}, err
	}
	response.Entries = entries(response.Entries, count)
	return response.Manifest, validate(response.envelope, false)
}

type Text struct {
	Text      string `json:"text"`
	Truncated bool   `json:"truncated"`
	Binary    bool   `json:"binary"`
	Size      int64  `json:"size"`
}

func (client Client) ReadText(ctx context.Context, path string, size int) (Text, error) {
	var response struct {
		envelope
		Text
	}
	if err := exchange(ctx, client, "read_text", map[string]any{"path": path, "max_bytes": size}, &response); err != nil {
		return Text{}, err
	}
	response.Text.Text, response.Truncated = limit(response.Text.Text, size, response.Truncated)
	return response.Text, validate(response.envelope, false)
}

type Render struct {
	Width  int    `json:"width"`
	Height int    `json:"height"`
	RGBA   []byte `json:"rgba_b64"`
	Pages  int    `json:"pages"`
	Kind   string `json:"kind"`
}

func (client Client) Render(ctx context.Context, path string, page, maxPx int) (Render, error) {
	var response struct {
		envelope
		Render
	}
	err := exchange(ctx, client, "render", map[string]any{"path": path, "page": page, "max_px": maxPx}, &response)
	if err != nil {
		return Render{}, err
	}
	return response.Render, validate(response.envelope, false)
}

type Session struct {
	Name    string          `json:"name"`
	Busy    bool            `json:"busy"`
	Started json.RawMessage `json:"started"`
	PID     int             `json:"pid"`
}

func (client Client) Sessions(ctx context.Context) ([]Session, error) {
	var response struct {
		envelope
		Sessions []Session `json:"sessions"`
	}
	if err := exchange(ctx, client, "sessions", nil, &response); err != nil {
		return nil, err
	}
	return response.Sessions, validate(response.envelope, false)
}

func (client Client) ResetSession(ctx context.Context, session string) error {
	return simple(ctx, client, "reset_session", map[string]any{"session": session})
}

type PreResult struct {
	Result json.RawMessage `json:"result"`
	State  json.RawMessage `json:"state"`
	OK     bool            `json:"-"`
	Error  *string         `json:"-"`
}

func (client Client) PreRun(ctx context.Context, code string, event, state json.RawMessage, timeout int) (PreResult, error) {
	var response struct {
		envelope
		PreResult
	}
	err := exchange(ctx, client, "pre_run", map[string]any{"code": code, "event": event, "state": state, "timeout_ms": timeout}, &response)
	if err != nil {
		return PreResult{}, err
	}
	if err := validate(response.envelope, true); err != nil {
		return PreResult{}, err
	}
	response.PreResult.OK, response.PreResult.Error = *response.envelope.OK, response.envelope.Error
	return response.PreResult, nil
}

func (client Client) PrepareHibernate(ctx context.Context) error {
	return simple(ctx, client, "prepare_hibernate", nil)
}
func (client Client) Shutdown(ctx context.Context) error { return simple(ctx, client, "shutdown", nil) }

type Ping struct {
	Load      json.RawMessage `json:"load"`
	MemFreeKB int64           `json:"mem_free_kb"`
}

func (client Client) Ping(ctx context.Context) (Ping, error) {
	var response struct {
		envelope
		Ping
	}
	if err := exchange(ctx, client, "ping", nil, &response); err != nil {
		return Ping{}, err
	}
	return response.Ping, validate(response.envelope, false)
}
