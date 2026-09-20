package twamp

import (
	"testing"
)

func TestResolveTarget(t *testing.T) {
	tests := []struct {
		name          string
		target        string
		defaultPort   int
		collectorAddr string
		expected      string
	}{
		{
			name:          "empty target",
			target:        "",
			defaultPort:   862,
			collectorAddr: "127.0.0.1:50051",
			expected:      "",
		},
		{
			name:          "target collector with default port",
			target:        "collector",
			defaultPort:   862,
			collectorAddr: "192.168.1.50:50051",
			expected:      "192.168.1.50:862",
		},
		{
			name:          "target auto with custom port",
			target:        "auto",
			defaultPort:   8620,
			collectorAddr: "collector.internal:50051",
			expected:      "collector.internal:8620",
		},
		{
			name:          "target collector with ipv6 collector",
			target:        "collector",
			defaultPort:   862,
			collectorAddr: "[::1]:50051",
			expected:      "[::1]:862",
		},
		{
			name:          "target collector with bare host collector",
			target:        "collector",
			defaultPort:   862,
			collectorAddr: "my-collector",
			expected:      "my-collector:862",
		},
		{
			name:          "bare IP without port",
			target:        "10.0.0.1",
			defaultPort:   862,
			collectorAddr: "irrelevant",
			expected:      "10.0.0.1:862",
		},
		{
			name:          "bare IP with custom port",
			target:        "10.0.0.1",
			defaultPort:   50060,
			collectorAddr: "irrelevant",
			expected:      "10.0.0.1:50060",
		},
		{
			name:          "explicit host and port preserved",
			target:        "10.0.0.1:9000",
			defaultPort:   862,
			collectorAddr: "irrelevant",
			expected:      "10.0.0.1:9000",
		},
		{
			name:          "explicit ipv6 host and port preserved",
			target:        "[2001:db8::1]:9999",
			defaultPort:   862,
			collectorAddr: "irrelevant",
			expected:      "[2001:db8::1]:9999",
		},
		{
			name:          "zero defaultPort falls back to 862",
			target:        "collector",
			defaultPort:   0,
			collectorAddr: "10.0.0.2:50051",
			expected:      "10.0.0.2:862",
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			actual := ResolveTarget(tc.target, tc.defaultPort, tc.collectorAddr)
			if actual != tc.expected {
				t.Fatalf("expected %q, got %q", tc.expected, actual)
			}
		})
	}
}
