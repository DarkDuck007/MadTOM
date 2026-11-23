package bridge

import (
	"bufio"
	"context"
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net"
	"strconv"
	"sync"
	"sync/atomic"
	"time"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

// Frame kinds exchanged over the local Python<->Go bridge
const (
	FrameKindModeSync   = 0x01
	FrameKindLogRecord  = 0x02
	FrameKindApiCall    = 0x03
	FrameKindTelemetry  = 0x04
	FrameKindJobEvent   = 0x05
	FrameKindTurnEvent  = 0x06
	FrameKindChatTrace  = 0x07
	FrameKindRpcRequest = 0x10
	FrameKindRpcReply   = 0x11
)

// ModeSync informs Python of current subscribers and store settings.
type ModeSync struct {
	StateEnabled bool `json:"state_enabled"`
	LogsEnabled  bool `json:"logs_enabled"`
	StoreEnabled bool `json:"store_enabled"`
}

type RpcMessage struct {
	ReqID   string          `json:"req_id"`
	Method  string          `json:"method"`
	Payload json.RawMessage `json:"payload"`
	Error   string          `json:"error,omitempty"`
}

var (
	errDisconnected = errors.New("bridge not connected")
	ErrTimeout      = errors.New("rpc timeout waiting for method")
)

// RPCError reports a rejected request from the Python bot.
type RPCError struct{ Message string }

func (e *RPCError) Error() string { return "python rpc error: " + e.Message }

type Dispatcher interface {
	OnLogRecord(rec *madtomv1.LogRecord)
	OnApiCallRecord(rec *madtomv1.NoxRawApiCallRecord)
	OnTelemetrySample(sample *madtomv1.NoxTelemetrySample)
	OnJobEvent(event *madtomv1.NoxJobProgressEvent)
	OnTurnEvent(event *madtomv1.NoxTurnTagEvent)
	OnChatTraceEvent(event *madtomv1.NoxChatTraceEvent)
}

// Client manages persistent connection to Python's local loopback server.
type Client struct {
	mu           sync.Mutex
	host         string
	port         int
	conn         net.Conn
	writer       *bufio.Writer
	disconnected chan struct{}
	dispatcher   Dispatcher
	pendingRpcs  map[string]chan *RpcMessage
	currentMode  ModeSync
	writeGate    chan struct{}
	ctx          context.Context
	cancel       context.CancelFunc
	start        sync.Once
	stopped      chan struct{}
	reqSeq       uint64
}

func NewClient(host string, port int, dispatcher Dispatcher) *Client {
	ctx, cancel := context.WithCancel(context.Background())
	return &Client{
		host:        host,
		port:        port,
		dispatcher:  dispatcher,
		pendingRpcs: make(map[string]chan *RpcMessage),
		writeGate:   make(chan struct{}, 1),
		ctx:         ctx,
		cancel:      cancel,
		stopped:     make(chan struct{}),
	}
}

// Start begins the auto-reconnecting background worker loop.
func (c *Client) Start() {
	c.start.Do(func() { go c.workerLoop() })
}

// Connected reports whether the bridge currently has a live transport.
func (c *Client) Connected() bool {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.conn != nil && c.ctx.Err() == nil
}

func (c *Client) workerLoop() {
	defer close(c.stopped)
	addr := net.JoinHostPort(c.host, strconv.Itoa(c.port))
	dialer := net.Dialer{Timeout: 2 * time.Second}
	for c.ctx.Err() == nil {
		conn, err := dialer.DialContext(c.ctx, "tcp", addr)
		if err != nil {
			timer := time.NewTimer(time.Second)
			select {
			case <-c.ctx.Done():
				timer.Stop()
				return
			case <-timer.C:
			}
			continue
		}

		c.mu.Lock()
		if c.ctx.Err() != nil {
			c.mu.Unlock()
			_ = conn.Close()
			return
		}
		c.conn = conn
		c.writer = bufio.NewWriter(conn)
		c.disconnected = make(chan struct{})
		c.mu.Unlock()

		c.sendModeSync(conn)
		log.Printf("[ControlBridge] Connected to Python bridge at %s", addr)
		c.readLoop(bufio.NewReader(conn))

		c.mu.Lock()
		_ = conn.Close()
		c.conn, c.writer = nil, nil
		close(c.disconnected)
		c.mu.Unlock()
		log.Print("[ControlBridge] Disconnected from Python bridge")
	}
}

func (c *Client) readLoop(reader *bufio.Reader) {
	for {
		var hdr [5]byte
		if _, err := io.ReadFull(reader, hdr[:]); err != nil {
			return
		}
		kind := hdr[0]
		length := binary.BigEndian.Uint32(hdr[1:5])
		payload := make([]byte, length)
		if _, err := io.ReadFull(reader, payload); err != nil {
			return
		}

		c.handleIncoming(kind, payload)
	}
}

func unmarshalBridgePayload(payload []byte, m proto.Message) error {
	opts := protojson.UnmarshalOptions{DiscardUnknown: true}
	if err := opts.Unmarshal(payload, m); err == nil {
		return nil
	}
	return proto.Unmarshal(payload, m)
}

func (c *Client) handleIncoming(kind byte, payload []byte) {
	switch kind {
	case FrameKindLogRecord:
		rec := &madtomv1.LogRecord{}
		if err := unmarshalBridgePayload(payload, rec); err == nil && c.dispatcher != nil {
			c.dispatcher.OnLogRecord(rec)
		}
	case FrameKindApiCall:
		rec := &madtomv1.NoxRawApiCallRecord{}
		if err := unmarshalBridgePayload(payload, rec); err == nil && c.dispatcher != nil {
			c.dispatcher.OnApiCallRecord(rec)
		}
	case FrameKindTelemetry:
		sample := &madtomv1.NoxTelemetrySample{}
		if err := unmarshalBridgePayload(payload, sample); err == nil && c.dispatcher != nil {
			c.dispatcher.OnTelemetrySample(sample)
		}
	case FrameKindJobEvent:
		event := &madtomv1.NoxJobProgressEvent{}
		if err := unmarshalBridgePayload(payload, event); err == nil && c.dispatcher != nil {
			c.dispatcher.OnJobEvent(event)
		}
	case FrameKindTurnEvent:
		event := &madtomv1.NoxTurnTagEvent{}
		if err := unmarshalBridgePayload(payload, event); err == nil && c.dispatcher != nil {
			c.dispatcher.OnTurnEvent(event)
		}
	case FrameKindChatTrace:
		event := &madtomv1.NoxChatTraceEvent{}
		if err := unmarshalBridgePayload(payload, event); err == nil && c.dispatcher != nil {
			c.dispatcher.OnChatTraceEvent(event)
		}
	case FrameKindRpcReply:
		var reply RpcMessage
		if err := json.Unmarshal(payload, &reply); err == nil {
			c.mu.Lock()
			ch, ok := c.pendingRpcs[reply.ReqID]
			delete(c.pendingRpcs, reply.ReqID)
			c.mu.Unlock()
			if ok {
				ch <- &reply
			}
		}
	}
}

// SetCaptureMode updates the capture state on the Python side.
func (c *Client) SetCaptureMode(stateEnabled, logsEnabled, storeEnabled bool) {
	c.mu.Lock()
	c.currentMode = ModeSync{
		StateEnabled: stateEnabled,
		LogsEnabled:  logsEnabled,
		StoreEnabled: storeEnabled,
	}
	conn := c.conn
	c.mu.Unlock()

	if conn != nil {
		c.sendModeSync(conn)
	}
}

func (c *Client) sendModeSync(conn net.Conn) {
	ctx, cancel := context.WithTimeout(c.ctx, 2*time.Second)
	defer cancel()
	if c.acquireWriter(ctx) != nil {
		return
	}
	defer c.releaseWriter()

	c.mu.Lock()
	mode := c.currentMode
	c.mu.Unlock()
	data, _ := json.Marshal(mode)
	_ = c.writeFrame(ctx, conn, FrameKindModeSync, data)
}

func (c *Client) acquireWriter(ctx context.Context) error {
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-c.ctx.Done():
		return errDisconnected
	case c.writeGate <- struct{}{}:
		return nil
	}
}

func (c *Client) releaseWriter() { <-c.writeGate }

// writeFrame requires the caller to hold the write gate.
func (c *Client) writeFrame(ctx context.Context, conn net.Conn, kind byte, data []byte) error {
	c.mu.Lock()
	writer := c.writer
	sameConnection := c.conn == conn && conn != nil
	c.mu.Unlock()
	if !sameConnection || c.ctx.Err() != nil {
		return errDisconnected
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	interrupted := make(chan struct{})
	stopInterrupt := context.AfterFunc(ctx, func() {
		_ = conn.SetWriteDeadline(time.Now())
		close(interrupted)
	})
	defer func() {
		if !stopInterrupt() {
			<-interrupted
		}
		_ = conn.SetWriteDeadline(time.Time{})
	}()

	if err := writeBufferedFrame(writer, kind, data); err != nil {
		// A partial frame cannot be retried on this connection.
		_ = conn.Close()
		return err
	}
	return nil
}

func writeBufferedFrame(writer *bufio.Writer, kind byte, data []byte) error {
	var hdr [5]byte
	hdr[0] = kind
	binary.BigEndian.PutUint32(hdr[1:5], uint32(len(data)))

	if _, err := writer.Write(hdr[:]); err != nil {
		return err
	}
	if _, err := writer.Write(data); err != nil {
		return err
	}
	return writer.Flush()
}

// CallRpc sends an RPC message to Python and waits for reply.
func (c *Client) CallRpc(method string, reqPayload interface{}, timeout time.Duration) (*RpcMessage, error) {
	return c.CallRpcContext(context.Background(), method, reqPayload, timeout)
}

// CallRpcContext removes abandoned requests when their gRPC caller navigates away.
func (c *Client) CallRpcContext(ctx context.Context, method string, reqPayload interface{}, timeout time.Duration) (*RpcMessage, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	ctx, cancel := context.WithTimeoutCause(ctx, timeout, ErrTimeout)
	defer cancel()
	contextError := func() error {
		if context.Cause(ctx) == ErrTimeout {
			return fmt.Errorf("%w %s", ErrTimeout, method)
		}
		return ctx.Err()
	}
	reqID := fmt.Sprintf("%d-%d", time.Now().UnixNano(), atomic.AddUint64(&c.reqSeq, 1))
	rawPayload, err := json.Marshal(reqPayload)
	if err != nil {
		return nil, err
	}

	msg := RpcMessage{
		ReqID:   reqID,
		Method:  method,
		Payload: rawPayload,
	}
	frameData, err := json.Marshal(msg)
	if err != nil {
		return nil, err
	}

	ch := make(chan *RpcMessage, 1)
	c.mu.Lock()
	conn, disconnected := c.conn, c.disconnected
	if conn == nil {
		c.mu.Unlock()
		return nil, errDisconnected
	}
	c.pendingRpcs[reqID] = ch
	c.mu.Unlock()
	defer func() {
		c.mu.Lock()
		delete(c.pendingRpcs, reqID)
		c.mu.Unlock()
	}()

	if err = c.acquireWriter(ctx); err == nil {
		err = c.writeFrame(ctx, conn, FrameKindRpcRequest, frameData)
		c.releaseWriter()
	}
	if err != nil {
		if ctx.Err() != nil {
			return nil, contextError()
		}
		return nil, err
	}

	var reply *RpcMessage
	select {
	case reply = <-ch:
	case <-disconnected:
		select {
		case reply = <-ch:
		default:
			return nil, errDisconnected
		}
	case <-c.ctx.Done():
		return nil, errDisconnected
	case <-ctx.Done():
		return nil, contextError()
	}
	if reply.Error != "" {
		return nil, &RPCError{Message: reply.Error}
	}
	return reply, nil
}

// Close stops the bridge client.
func (c *Client) Close() {
	c.cancel()
	c.mu.Lock()
	if c.conn != nil {
		_ = c.conn.Close()
	}
	c.mu.Unlock()
	c.start.Do(func() { close(c.stopped) })
	<-c.stopped
}
