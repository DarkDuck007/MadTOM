package bridge

import (
	"bufio"
	"context"
	"encoding/binary"
	"io"
	"net"
	"testing"
	"time"

	pb "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

type traceDispatcher struct {
	trace *pb.NoxChatTraceEvent
	call  *pb.NoxRawApiCallRecord
}

func (d *traceDispatcher) OnLogRecord(*pb.LogRecord)                    {}
func (d *traceDispatcher) OnApiCallRecord(call *pb.NoxRawApiCallRecord) { d.call = call }
func (d *traceDispatcher) OnTelemetrySample(*pb.NoxTelemetrySample)     {}
func (d *traceDispatcher) OnJobEvent(*pb.NoxJobProgressEvent)           {}
func (d *traceDispatcher) OnTurnEvent(*pb.NoxTurnTagEvent)              {}
func (d *traceDispatcher) OnChatTraceEvent(event *pb.NoxChatTraceEvent) { d.trace = event }

func TestBridgePreservesPresentChatZeroAndTraceFrames(t *testing.T) {
	d := new(traceDispatcher)
	c := NewClient("unused", 0, d)
	c.handleIncoming(FrameKindChatTrace, []byte(`{"event_id":"event","trace_context":{"chat_id":0,"run_id":"run"},"kind":"tool_result","content":"done","timestamp_unix_nano":12,"attributes":{"tool_call_id":"tool-1"}}`))
	if d.trace == nil || d.trace.TraceContext.ChatId == nil || d.trace.TraceContext.GetChatId() != 0 || d.trace.Attributes["tool_call_id"] != "tool-1" {
		t.Fatal(d.trace)
	}
	c.handleIncoming(FrameKindApiCall, []byte(`{"call_id":"call","trace_context":{"chat_id":0,"run_id":"run","step_index":2},"raw_response_json":"{}"}`))
	if d.call == nil || d.call.TraceContext.ChatId == nil || d.call.TraceContext.GetStepIndex() != 2 {
		t.Fatal(d.call)
	}
}

func TestCanceledBridgeRpcDropsPendingRequest(t *testing.T) {
	local, remote := net.Pipe()
	defer local.Close()
	defer remote.Close()
	c := NewClient("unused", 0, nil)
	c.conn = local
	c.writer = bufio.NewWriter(local)
	read := make(chan struct{})
	go func() {
		var header [5]byte
		if _, err := io.ReadFull(remote, header[:]); err != nil {
			return
		}
		_, _ = io.CopyN(io.Discard, remote, int64(binary.BigEndian.Uint32(header[1:])))
		close(read)
	}()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	done := make(chan error, 1)
	go func() {
		_, err := c.CallRpcContext(ctx, "list_chats", &pb.NoxListChatsRequest{Limit: 100}, time.Second)
		done <- err
	}()
	select {
	case <-read:
		cancel()
	case <-time.After(time.Second):
		t.Fatal("request was not sent")
	}
	select {
	case err := <-done:
		if err != context.Canceled {
			t.Fatal(err)
		}
	case <-time.After(time.Second):
		t.Fatal("canceled RPC did not finish")
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	if len(c.pendingRpcs) != 0 {
		t.Fatal("canceled pending RPC leaked")
	}
}
