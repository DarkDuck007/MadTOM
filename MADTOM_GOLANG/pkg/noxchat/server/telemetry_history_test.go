package server

import "testing"

const sec = int64(1_000_000_000)

func TestTelemetryStoreKeepsRawTailAndRollsUpOlderData(t *testing.T) {
	st := newTelemetryStore(120, 60, 100) // 2 min raw, 1-min buckets
	base := int64(1_764_000_000) * sec    // bucket aligned
	for i := int64(0); i < 600; i++ {     // 10 minutes at 1 Hz, value = second index
		st.add("llm.tokens.in", base+i*sec, float64(i))
	}
	m := st.metrics["llm.tokens.in"]
	if len(m.raw) != 120 || m.raw[0].ts != base+480*sec {
		t.Fatalf("raw tier: %d points from %d", len(m.raw), m.raw[0].ts)
	}
	if len(m.rollup) != 9 { // minutes 0..8 closed, minute 9 still open
		t.Fatalf("rollup buckets: %d", len(m.rollup))
	}
	b := m.rollup[2]
	if b.ts != base+120*sec || b.val != 149.5 || b.min != 120 || b.max != 179 {
		t.Fatalf("bucket 2: %+v", b)
	}

	// A 10-minute window: rollup buckets for minutes 0..7 (before the raw tier), then 120 raw samples.
	pts := st.rangePoints("llm.tokens.in", base, base+600*sec)
	if len(pts) != 8+120 {
		t.Fatalf("range points: %d", len(pts))
	}
	for i := 1; i < len(pts); i++ {
		if pts[i].ts <= pts[i-1].ts {
			t.Fatalf("points out of order at %d", i)
		}
	}
	// A window inside the raw tier uses raw samples only.
	if got := st.rangePoints("llm.tokens.in", base+500*sec, base+509*sec); len(got) != 10 {
		t.Fatalf("raw-only window: %d", len(got))
	}
	if st.rangePoints("missing", 0, base) != nil {
		t.Fatal("unknown metric should be empty")
	}
}

func TestTelemetryStoreRollupIsBounded(t *testing.T) {
	st := newTelemetryStore(10, 1, 5)
	for i := int64(0); i < 100; i++ {
		st.add("m", i*sec, 1)
	}
	if m := st.metrics["m"]; len(m.rollup) != 5 || len(m.raw) != 10 {
		t.Fatalf("rollup=%d raw=%d", len(m.rollup), len(m.raw))
	}
	off := newTelemetryStore(10, 60, 0)
	off.add("m", 0, 1)
	if len(off.metrics["m"].rollup) != 0 {
		t.Fatal("rollup disabled")
	}
}
