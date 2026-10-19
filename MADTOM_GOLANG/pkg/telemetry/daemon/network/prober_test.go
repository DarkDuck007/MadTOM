package network

import (
	"context"
	"testing"
)

func TestNetworkProber_Capabilities(t *testing.T) {
	p := NewNetworkProber()
	if p.BootID() == "" {
		t.Errorf("expected non-empty boot id")
	}

	caps := p.ProbeCapabilities(context.Background())
	if caps == nil {
		t.Fatalf("expected non-nil capabilities payload")
	}
	if !caps.HasNetlink {
		t.Errorf("expected HasNetlink to be true")
	}
}

func TestNetworkProber_InterfacesAndAddresses(t *testing.T) {
	p := NewNetworkProber()
	ifaces, err := p.ProbeInterfaces(context.Background(), "test-node")
	if err != nil {
		t.Fatalf("ProbeInterfaces failed: %v", err)
	}
	if ifaces == nil || len(ifaces.Interfaces) == 0 {
		t.Logf("Warning: no network interfaces found (could be containerized env)")
	} else {
		foundLo := false
		for _, iface := range ifaces.Interfaces {
			if iface.Name == "lo" {
				foundLo = true
				break
			}
		}
		if !foundLo {
			t.Logf("Notice: loopback interface 'lo' not found in %d interfaces", len(ifaces.Interfaces))
		}
	}

	addrs, err := p.ProbeAddressesAndNeighbors(context.Background())
	if err != nil {
		t.Fatalf("ProbeAddressesAndNeighbors failed: %v", err)
	}
	if addrs == nil {
		t.Fatalf("expected non-nil addresses payload")
	}
}

func TestNetworkProber_RoutingTable(t *testing.T) {
	p := NewNetworkProber()
	routes, err := p.ProbeRoutingTable(context.Background())
	if err != nil {
		t.Fatalf("ProbeRoutingTable failed: %v", err)
	}
	if routes == nil {
		t.Fatalf("expected non-nil routing payload")
	}
	t.Logf("Discovered %d routing table entries", len(routes.Routes))
}

func TestNetworkProber_ParseNftablesJson(t *testing.T) {
	sampleNft := []byte(`{
		"nftables": [
			{"metainfo": {"version": "1.0.2", "release_name": "Fearless Foobar"}},
			{"table": {"family": "inet", "name": "filter", "handle": 1}},
			{"chain": {"family": "inet", "table": "filter", "name": "input", "handle": 1, "type": "filter", "hook": "input", "prio": 0, "policy": "accept"}},
			{"rule": {"family": "inet", "table": "filter", "chain": "input", "handle": 2, "comment": "allow ssh", "expr": [{"match": {"left": {"payload": {"protocol": "tcp", "field": "dport"}}, "op": "==", "right": 22}}, {"accept": null}]}}
		]
	}`)

	payload, err := ParseNftablesRulesetJson(sampleNft)
	if err != nil {
		t.Fatalf("ParseNftablesRulesetJson failed: %v", err)
	}

	if payload.TotalRuleCount != 1 {
		t.Errorf("expected 1 rule, got %d", payload.TotalRuleCount)
	}
	if len(payload.Tables) != 1 || len(payload.Tables[0].Chains) != 1 || len(payload.Tables[0].Chains[0].Rules) != 1 {
		t.Fatalf("nested structure mismatch: tables=%d", len(payload.Tables))
	}

	rule := payload.Tables[0].Chains[0].Rules[0]
	if rule.Comment != "allow ssh" {
		t.Errorf("expected comment 'allow ssh', got %q", rule.Comment)
	}
	if rule.Verdict != "accept" {
		t.Errorf("expected verdict 'accept', got %q", rule.Verdict)
	}
}

func TestNetworkProber_Sockets(t *testing.T) {
	p := NewNetworkProber()
	socks, err := p.ProbeSockets(context.Background(), 100)
	if err != nil {
		t.Fatalf("ProbeSockets failed: %v", err)
	}
	if socks == nil {
		t.Fatalf("expected non-nil sockets payload")
	}
	t.Logf("Discovered %d sockets (total established: %d, listen: %d)",
		len(socks.Sockets), socks.TotalEstablished, socks.TotalListening)
}

func TestNetworkProber_TargetValidation(t *testing.T) {
	valid := []string{"127.0.0.1", "::1", "google.com", "gateway-01.lan"}
	for _, v := range valid {
		if err := ValidateProbeTarget(v); err != nil {
			t.Errorf("expected %q to be valid, got: %v", v, err)
		}
	}

	invalid := []string{"", "127.0.0.1; rm -rf /", "foo | bar", "test`whoami`"}
	for _, inv := range invalid {
		if err := ValidateProbeTarget(inv); err == nil {
			t.Errorf("expected %q to be rejected, but passed", inv)
		}
	}
}
