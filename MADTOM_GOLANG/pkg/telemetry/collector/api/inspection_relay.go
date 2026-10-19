package api

import (
	"context"
	"io"
	"os"
	"strings"
	"sync"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"github.com/DarkDuck007/madtom/pkg/telemetry/collector/registry"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/status"
)

// NetworkInspectionRelayServer relays NetworkInspectionService requests from UI clients
// either to local node probers (if node matches this collector) or down to monitored node daemons.
type NetworkInspectionRelayServer struct {
	madtomv1.UnimplementedNetworkInspectionServiceServer
	mu       sync.Mutex
	reg      *registry.Registry
	conns    map[string]*grpc.ClientConn // target address -> conn
}

func NewNetworkInspectionRelayServer(reg *registry.Registry) *NetworkInspectionRelayServer {
	return &NetworkInspectionRelayServer{
		reg:   reg,
		conns: make(map[string]*grpc.ClientConn),
	}
}

func isLocalNode(nodeID string) bool {
	h, err := os.Hostname()
	if err == nil && h != "" {
		if strings.EqualFold(nodeID, h) ||
			strings.Contains(strings.ToLower(h), strings.ToLower(nodeID)) ||
			strings.Contains(strings.ToLower(nodeID), strings.ToLower(h)) {
			return true
		}
	}
	return strings.EqualFold(nodeID, "localhost") || nodeID == "127.0.0.1" || nodeID == "::1"
}

func (s *NetworkInspectionRelayServer) getClient(nodeID string) (madtomv1.NetworkInspectionServiceClient, string, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	addr := s.reg.GetNodeAddress(nodeID)
	if addr == "" {
		if isLocalNode(nodeID) {
			addr = "127.0.0.1:50052"
		} else {
			return nil, "", status.Errorf(codes.NotFound, "node daemon address not registered for node %q", nodeID)
		}
	}

	conn, exists := s.conns[addr]
	if !exists {
		var err error
		conn, err = grpc.NewClient(addr, grpc.WithTransportCredentials(insecure.NewCredentials()))
		if err != nil {
			return nil, addr, status.Errorf(codes.Unavailable, "failed to dial node daemon at %s: %v", addr, err)
		}
		s.conns[addr] = conn
	}

	return madtomv1.NewNetworkInspectionServiceClient(conn), addr, nil
}

func (s *NetworkInspectionRelayServer) removeConn(addr string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if conn, exists := s.conns[addr]; exists {
		delete(s.conns, addr)
		_ = conn.Close()
	}
}

func (s *NetworkInspectionRelayServer) StreamNetworkTopology(req *madtomv1.TopologyStreamRequest, stream grpc.ServerStreamingServer[madtomv1.TopologyEvent]) error {
	client, addr, err := s.getClient(req.NodeId)
	if err != nil {
		return err
	}

	subStream, err := client.StreamNetworkTopology(stream.Context(), req)
	if err != nil {
		s.removeConn(addr)
		return err
	}

	for {
		ev, err := subStream.Recv()
		if err == io.EOF {
			return nil
		}
		if err != nil {
			s.removeConn(addr)
			return err
		}
		if err := stream.Send(ev); err != nil {
			return err
		}
	}
}

func (s *NetworkInspectionRelayServer) RenewInspectionLease(ctx context.Context, req *madtomv1.LeaseRenewalRequest) (*madtomv1.LeaseRenewalResponse, error) {
	client, addr, err := s.getClient(req.NodeId)
	if err != nil {
		return nil, err
	}
	resp, err := client.RenewInspectionLease(ctx, req)
	if err != nil {
		s.removeConn(addr)
		return nil, err
	}
	return resp, nil
}

func (s *NetworkInspectionRelayServer) StreamDiagnosticProbe(req *madtomv1.DiagnosticProbeRequest, stream grpc.ServerStreamingServer[madtomv1.DiagnosticProbeOutput]) error {
	client, addr, err := s.getClient(req.NodeId)
	if err != nil {
		return err
	}

	subStream, err := client.StreamDiagnosticProbe(stream.Context(), req)
	if err != nil {
		s.removeConn(addr)
		return err
	}

	for {
		out, err := subStream.Recv()
		if err == io.EOF {
			return nil
		}
		if err != nil {
			s.removeConn(addr)
			return err
		}
		if err := stream.Send(out); err != nil {
			return err
		}
	}
}
