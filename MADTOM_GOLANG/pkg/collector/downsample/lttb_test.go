package downsample

import (
	"math"
	"testing"

	"github.com/DarkDuck007/madtom/pkg/collector/storage"
)

func TestLTTBDownsampling(t *testing.T) {
	const totalPoints = 1000
	const targetPoints = 50

	data := make([]storage.Point, totalPoints)
	for i := 0; i < totalPoints; i++ {
		// Sine wave with a huge spike at index 500
		val := math.Sin(float64(i) * 0.1)
		if i == 500 {
			val = 100.0 // extreme peak
		}
		data[i] = storage.Point{
			TimestampUnixNano: int64(i) * 1_000_000,
			Value:             val,
		}
	}

	sampled := DownsampleLTTB(data, targetPoints)

	if len(sampled) != targetPoints {
		t.Fatalf("expected %d points, got %d", targetPoints, len(sampled))
	}

	if sampled[0] != data[0] {
		t.Fatalf("first point mismatch")
	}
	if sampled[len(sampled)-1] != data[len(data)-1] {
		t.Fatalf("last point mismatch")
	}

	// Verify the extreme spike was preserved
	foundSpike := false
	for _, pt := range sampled {
		if pt.Value >= 99.0 {
			foundSpike = true
			break
		}
	}

	if !foundSpike {
		t.Fatalf("LTTB failed to preserve extreme peak of 100.0")
	}
}
