package ffmpeg

import (
	"math"
	"github.com/DarkDuck007/madtom/pkg/squeeze/types"
	"strings"
	"testing"
)

func TestProgress(t *testing.T) {
	var got []types.Progress
	err := ParseProgress(strings.NewReader("frame=1420\nfps=84.20\nbitrate=4250.0kbits/s\ntotal_size=12451840\nout_time_us=42500000\nspeed=2.00x\nprogress=continue\nout_time_us=120000000\nprogress=end\n"), 100, func(p types.Progress) { got = append(got, p) })
	if err != nil || len(got) != 2 {
		t.Fatalf("%v %#v", err, got)
	}
	p := got[0]
	if p.Frame != 1420 || p.FPS != 84.2 || p.Bitrate != 4250 || p.TotalSize != 12451840 || p.Progress != 42.5 || p.ETA != "00:00:29" {
		t.Fatalf("unexpected progress: %+v", p)
	}
	if got[1].Progress != 100 || got[1].ETA != "00:00:00" {
		t.Fatal(got[1])
	}
}
func TestProgressUnknown(t *testing.T) {
	err := ParseProgress(strings.NewReader("fps=NaN\nbitrate=N/A\nspeed=0x\nout_time_us=-1\nprogress=end\n"), 0, func(p types.Progress) {
		if p.ETA != "unknown" || p.Progress != 0 || math.IsNaN(p.FPS) {
			t.Fatal(p)
		}
	})
	if err != nil {
		t.Fatal(err)
	}
}
