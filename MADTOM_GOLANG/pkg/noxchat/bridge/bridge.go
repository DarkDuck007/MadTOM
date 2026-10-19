package bridge

import (
	"bufio"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net"
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

type Dispatcher interface {
	OnLogRecord(rec *madtomv1.LogRecord)
	OnApiCallRecord(rec *madtomv1.NoxRawApiCallRecord)
	OnTelemetrySample(sample *madtomv1.NoxTelemetrySample)
	OnJobEvent(event *madtomv1.NoxJobProgressEvent)
	OnTurnEvent(event *madtomv1.NoxTurnTagEvent)
}

// Client manages persistent connection to Python's local loopback server.
type Client struct {
	mu           sync.Mutex
	host         string
	port         int
	conn         net.Conn
	reader       *bufio.Reader
	writer       *bufio.Writer
	dispatcher   Dispatcher
	closed       bool
	pendingRpcs  map[string]chan *RpcMessage
	currentMode  ModeSync
	reconnectSig chan struct{}
	reqSeq       uint64
}

func NewClient(host string, port int, dispatcher Dispatcher) *Client {
	return &Client{
		host:         host,
		port:         port,
		dispatcher:   dispatcher,
		pendingRpcs:  make(map[string]chan *RpcMessage),
		reconnectSig: make(chan struct{}, 1),
	}
}

// Start begins the auto-reconnecting background worker loop.
func (c *Client) Start() {
	go c.workerLoop()
}

func (c *Client) workerLoop() {
	for !c.closed {
		addr := fmt.Sprintf("%s:%d", c.host, c.port)
		conn, err := net.DialTimeout("tcp", addr, 2*time.Second)
		if err != nil {
			time.Sleep(1 * time.Second)
			continue
		}

		c.mu.Lock()
		c.conn = conn
		c.reader = bufio.NewReader(conn)
		c.writer = bufio.NewWriter(conn)
		// Re-sync current mode upon connect
		_ = c.sendModeSyncLocked(c.currentMode)
		c.mu.Unlock()

		log.Printf("[NoxChat_Probe] Connected to Python bridge at %s", addr)
		c.readLoop()
		log.Printf("[NoxChat_Probe] Disconnected from Python bridge, reconnecting...")
	}
}

func (c *Client) readLoop() {
	for {
		var hdr [5]byte
		if _, err := io.ReadFull(c.reader, hdr[:]); err != nil {
			break
		}
		kind := hdr[0]
		length := binary.BigEndian.Uint32(hdr[1:5])
		payload := make([]byte, length)
		if _, err := io.ReadFull(c.reader, payload); err != nil {
			break
		}

		c.handleIncoming(kind, payload)
	}

	c.mu.Lock()
	if c.conn != nil {
		_ = c.conn.Close()
		c.conn = nil
	}
	c.mu.Unlock()
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
	defer c.mu.Unlock()

	c.currentMode = ModeSync{
		StateEnabled: stateEnabled,
		LogsEnabled:  logsEnabled,
		StoreEnabled: storeEnabled,
	}

	if c.conn != nil {
		_ = c.sendModeSyncLocked(c.currentMode)
	}
}

func (c *Client) sendModeSyncLocked(m ModeSync) error {
	data, _ := json.Marshal(m)
	return c.writeFrameLocked(FrameKindModeSync, data)
}

func (c *Client) writeFrameLocked(kind byte, data []byte) error {
	if c.conn == nil {
		return fmt.Errorf("bridge not connected")
	}
	var hdr [5]byte
	hdr[0] = kind
	binary.BigEndian.PutUint32(hdr[1:5], uint32(len(data)))

	if _, err := c.writer.Write(hdr[:]); err != nil {
		return err
	}
	if _, err := c.writer.Write(data); err != nil {
		return err
	}
	return c.writer.Flush()
}

// CallRpc sends an RPC message to Python and waits for reply.
func (c *Client) CallRpc(method string, reqPayload interface{}, timeout time.Duration) (*RpcMessage, error) {
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
	c.pendingRpcs[reqID] = ch
	err = c.writeFrameLocked(FrameKindRpcRequest, frameData)
	c.mu.Unlock()

	if err != nil {
		c.mu.Lock()
		delete(c.pendingRpcs, reqID)
		c.mu.Unlock()
		return nil, err
	}

	select {
	case reply := <-ch:
		if reply.Error != "" {
			return nil, fmt.Errorf("python rpc error: %s", reply.Error)
		}
		return reply, nil
	case <-time.After(timeout):
		c.mu.Lock()
		delete(c.pendingRpcs, reqID)
		c.mu.Unlock()
		return nil, fmt.Errorf("rpc timeout waiting for method %s", method)
	}
}

// Close stops the bridge client.
func (c *Client) Close() {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.closed = true
	if c.conn != nil {
		_ = c.conn.Close()
	}
}
