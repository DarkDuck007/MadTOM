package network

import (
	"context"
	"fmt"
	"log"
	"strings"
	"sync"
	"time"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
)

// NetworkInspectionServer implements madtomv1.NetworkInspectionServiceServer
type NetworkInspectionServer struct {
	madtomv1.UnimplementedNetworkInspectionServiceServer
	mu       sync.Mutex
	nodeID   string
	prober   *NetworkProber
	leases   map[string]time.Time // session_id -> expiration
}

func NewNetworkInspectionServer(nodeID string) *NetworkInspectionServer {
	return &NetworkInspectionServer{
		nodeID: nodeID,
		prober: NewNetworkProber(),
		leases: make(map[string]time.Time),
	}
}

func (s *NetworkInspectionServer) StreamNetworkTopology(req *madtomv1.TopologyStreamRequest, stream grpc.ServerStreamingServer[madtomv1.TopologyEvent]) error {
	if req.NodeId != "" && !strings.EqualFold(req.NodeId, s.nodeID) {
		return status.Errorf(codes.NotFound, "requested node id %q does not match daemon node id %q", req.NodeId, s.nodeID)
	}

	ctx := stream.Context()
	sessionID := req.SessionId
	if sessionID == "" {
		sessionID = fmt.Sprintf("sess-%d", time.Now().UnixNano())
	}

	leaseDuration := 15 * time.Second
	if req.LeaseSeconds > 0 && req.LeaseSeconds <= 60 {
		leaseDuration = time.Duration(req.LeaseSeconds) * time.Second
	}

	s.mu.Lock()
	s.leases[sessionID] = time.Now().Add(leaseDuration)
	s.mu.Unlock()

	defer func() {
		s.mu.Lock()
		delete(s.leases, sessionID)
		s.mu.Unlock()
	}()

	var seq uint64

	sendEvent := func(stage madtomv1.TopologyStage, desc string, progress float64, payload func(*madtomv1.TopologyEvent)) error {
		seq++
		ev := &madtomv1.TopologyEvent{
			SessionId:         sessionID,
			NodeId:            s.nodeID,
			Stage:             stage,
			StageDescription:  desc,
			ProgressPct:       progress,
			TimestampUnixNano: time.Now().UnixNano(),
			SequenceNumber:    seq,
		}
		if payload != nil {
			payload(ev)
		}
		return stream.Send(ev)
	}

	// 1. Stage: Capabilities
	if err := sendEvent(madtomv1.TopologyStage_TOPOLOGY_STAGE_CAPABILITIES, "Capabilities · Checking netlink and firewall support", 10.0, func(e *madtomv1.TopologyEvent) {
		caps := s.prober.ProbeCapabilities(ctx)
		e.Payload = &madtomv1.TopologyEvent_Capabilities{Capabilities: caps}
	}); err != nil {
		return err
	}

	// 2. Stage: Interfaces
	if err := sendEvent(madtomv1.TopologyStage_TOPOLOGY_STAGE_INTERFACES, "Interfaces · RTM_GETLINK dump", 25.0, func(e *madtomv1.TopologyEvent) {
		ifaces, err := s.prober.ProbeInterfaces(ctx, s.nodeID)
		if err == nil && ifaces != nil {
			e.Payload = &madtomv1.TopologyEvent_Interfaces{Interfaces: ifaces}
		}
	}); err != nil {
		return err
	}

	// 3. Stage: Addresses & Neighbors
	if err := sendEvent(madtomv1.TopologyStage_TOPOLOGY_STAGE_ADDRESSES_NEIGHBORS, "Addresses & Neighbors · RTM_GETADDR and RTM_GETNEIGH dump", 45.0, func(e *madtomv1.TopologyEvent) {
		addrs, err := s.prober.ProbeAddressesAndNeighbors(ctx)
		if err == nil && addrs != nil {
			e.Payload = &madtomv1.TopologyEvent_AddressesNeighbors{AddressesNeighbors: addrs}
		}
	}); err != nil {
		return err
	}

	// 4. Stage: Routing
	if err := sendEvent(madtomv1.TopologyStage_TOPOLOGY_STAGE_ROUTING, "Routing · RTM_GETROUTE dump", 65.0, func(e *madtomv1.TopologyEvent) {
		routing, err := s.prober.ProbeRoutingTable(ctx)
		if err == nil && routing != nil {
			e.Payload = &madtomv1.TopologyEvent_Routing{Routing: routing}
		}
	}); err != nil {
		return err
	}

	// 5. Stage: Firewall
	if req.IncludeFirewall {
		if err := sendEvent(madtomv1.TopologyStage_TOPOLOGY_STAGE_FIREWALL, "Firewall · Running nft -j -a list ruleset", 80.0, func(e *madtomv1.TopologyEvent) {
			fw, err := s.prober.ProbeFirewall(ctx)
			if err == nil && fw != nil {
				e.Payload = &madtomv1.TopologyEvent_Firewall{Firewall: fw}
			}
		}); err != nil {
			return err
		}
	}

	// 6. Stage: Sockets
	if req.IncludeSockets {
		if err := sendEvent(madtomv1.TopologyStage_TOPOLOGY_STAGE_SOCKETS, "Sockets · Scanning active TCP socket descriptors", 95.0, func(e *madtomv1.TopologyEvent) {
			socks, err := s.prober.ProbeSockets(ctx, req.MaxSockets)
			if err == nil && socks != nil {
				e.Payload = &madtomv1.TopologyEvent_Sockets{Sockets: socks}
			}
		}); err != nil {
			return err
		}
	}

	// Complete Initial Dump
	if err := sendEvent(madtomv1.TopologyStage_TOPOLOGY_STAGE_COMPLETE, "Live · Active streaming", 100.0, nil); err != nil {
		return err
	}

	// Continuous 1Hz loop guarded by lease expiration and client context
	ticker := time.NewTicker(1 * time.Second)
	defer ticker.Stop()

	for {
		select {
		case <-ctx.Done():
			return nil
		case <-ticker.C:
			// Check lease
			s.mu.Lock()
			exp, ok := s.leases[sessionID]
			s.mu.Unlock()
			if !ok || time.Now().After(exp) {
				log.Printf("[NetworkInspection] Session lease expired for %s", sessionID)
				return nil
			}

			// Stream 1Hz traffic delta
			ifaces, err := s.prober.ProbeInterfaces(ctx, s.nodeID)
			if err != nil || ifaces == nil {
				continue
			}

			trafficRates := make(map[string]*madtomv1.InterfaceTrafficRate)
			for _, iface := range ifaces.Interfaces {
				trafficRates[iface.Name] = &madtomv1.InterfaceTrafficRate{
					RxBytesPerSec:   iface.RxBytesPerSec,
					TxBytesPerSec:   iface.TxBytesPerSec,
					RxDroppedDelta:  iface.RxDropped,
					TxDroppedDelta:  iface.TxDropped,
					RxErrorsDelta:   iface.RxErrors,
					TxErrorsDelta:   iface.TxErrors,
				}
			}

			seq++
			ev := &madtomv1.TopologyEvent{
				SessionId:         sessionID,
				NodeId:            s.nodeID,
				Stage:             madtomv1.TopologyStage_TOPOLOGY_STAGE_COMPLETE,
				StageDescription:  "Live · 1Hz delta update",
				ProgressPct:       100.0,
				TimestampUnixNano: time.Now().UnixNano(),
				SequenceNumber:    seq,
				Payload: &madtomv1.TopologyEvent_Traffic{
					Traffic: &madtomv1.TrafficDeltaPayload{
						ElapsedMs:      1000,
						InterfaceRates: trafficRates,
					},
				},
			}

			if err := stream.Send(ev); err != nil {
				return err
			}
		}
	}
}

func (s *NetworkInspectionServer) RenewInspectionLease(ctx context.Context, req *madtomv1.LeaseRenewalRequest) (*madtomv1.LeaseRenewalResponse, error) {
	if req.NodeId != "" && !strings.EqualFold(req.NodeId, s.nodeID) {
		return nil, status.Errorf(codes.NotFound, "requested node id %q does not match daemon node id %q", req.NodeId, s.nodeID)
	}

	s.mu.Lock()
	defer s.mu.Unlock()

	sessionID := req.SessionId
	leaseDuration := 15 * time.Second
	if req.LeaseSeconds > 0 && req.LeaseSeconds <= 60 {
		leaseDuration = time.Duration(req.LeaseSeconds) * time.Second
	}

	newExp := time.Now().Add(leaseDuration)
	s.leases[sessionID] = newExp

	return &madtomv1.LeaseRenewalResponse{
		SessionId:       sessionID,
		Active:          true,
		ExpiresUnixNano: newExp.UnixNano(),
	}, nil
}

func (s *NetworkInspectionServer) StreamDiagnosticProbe(req *madtomv1.DiagnosticProbeRequest, stream grpc.ServerStreamingServer[madtomv1.DiagnosticProbeOutput]) error {
	if req.NodeId != "" && !strings.EqualFold(req.NodeId, s.nodeID) {
		return status.Errorf(codes.NotFound, "requested node id %q does not match daemon node id %q", req.NodeId, s.nodeID)
	}

	ctx := stream.Context()
	return s.prober.RunDiagnosticProbe(ctx, req, func(out *madtomv1.DiagnosticProbeOutput) error {
		return stream.Send(out)
	})
}
