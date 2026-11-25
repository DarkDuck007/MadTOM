package server

// In-memory telemetry history for QueryTelemetryRange: the last N raw samples per metric (1 Hz from NoxChat)
// plus a coarse rollup tier (mean/min/max per bucket) so 6H/24H/7D views have data. Both are bounded by
// config (control_center.telemetry_*); the history is lost when the probe restarts.

type historyPoint struct {
	ts       int64
	val      float64
	min, max float64
}

type rollupBucket struct {
	start      int64
	sum        float64
	n          int
	min, max   float64
	hasSamples bool
}

type metricHistory struct {
	raw    []historyPoint
	rollup []historyPoint
	cur    rollupBucket
}

type telemetryStore struct {
	rawPoints    int
	rollupNanos  int64
	rollupPoints int
	metrics      map[string]*metricHistory
}

func newTelemetryStore(rawPoints, rollupSeconds, rollupPoints int) *telemetryStore {
	if rawPoints < 1 {
		rawPoints = 1
	}
	if rollupSeconds < 1 {
		rollupSeconds = 60
	}
	if rollupPoints < 0 {
		rollupPoints = 0
	}
	return &telemetryStore{
		rawPoints:    rawPoints,
		rollupNanos:  int64(rollupSeconds) * 1_000_000_000,
		rollupPoints: rollupPoints,
		metrics:      make(map[string]*metricHistory),
	}
}

func (t *telemetryStore) add(name string, ts int64, v float64) {
	m := t.metrics[name]
	if m == nil {
		m = &metricHistory{}
		t.metrics[name] = m
	}
	m.raw = append(m.raw, historyPoint{ts: ts, val: v, min: v, max: v})
	if len(m.raw) > t.rawPoints {
		m.raw = append(m.raw[:0], m.raw[len(m.raw)-t.rawPoints:]...)
	}
	if t.rollupPoints == 0 {
		return
	}
	start := ts - ts%t.rollupNanos
	if m.cur.hasSamples && start != m.cur.start {
		t.flush(m)
	}
	if !m.cur.hasSamples {
		m.cur = rollupBucket{start: start, min: v, max: v, hasSamples: true}
	}
	m.cur.sum += v
	m.cur.n++
	if v < m.cur.min {
		m.cur.min = v
	}
	if v > m.cur.max {
		m.cur.max = v
	}
}

func (t *telemetryStore) flush(m *metricHistory) {
	if !m.cur.hasSamples || m.cur.n == 0 {
		return
	}
	m.rollup = append(m.rollup, historyPoint{
		ts: m.cur.start, val: m.cur.sum / float64(m.cur.n), min: m.cur.min, max: m.cur.max,
	})
	if len(m.rollup) > t.rollupPoints {
		m.rollup = append(m.rollup[:0], m.rollup[len(m.rollup)-t.rollupPoints:]...)
	}
	m.cur = rollupBucket{}
}

// rangePoints returns the points of a metric in [start, end]: rollup buckets for the part older than the raw
// tier, then the raw samples.
func (t *telemetryStore) rangePoints(name string, start, end int64) []historyPoint {
	m := t.metrics[name]
	if m == nil {
		return nil
	}
	rawFrom := int64(1<<63 - 1)
	if len(m.raw) > 0 {
		rawFrom = m.raw[0].ts
	}
	var out []historyPoint
	if start < rawFrom {
		for _, p := range m.rollup {
			if p.ts >= start && p.ts <= end && p.ts+t.rollupNanos <= rawFrom {
				out = append(out, p)
			}
		}
	}
	for _, p := range m.raw {
		if p.ts >= start && p.ts <= end {
			out = append(out, p)
		}
	}
	return out
}
