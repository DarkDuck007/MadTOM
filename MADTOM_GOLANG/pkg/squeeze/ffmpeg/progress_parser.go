package ffmpeg

import (
	"bufio"
	"fmt"
	"io"
	"math"
	"github.com/DarkDuck007/madtom/pkg/squeeze/types"
	"strconv"
	"strings"
)

func ParseProgress(r io.Reader, duration float64, emit func(types.Progress)) error {
	scanner := bufio.NewScanner(r)
	scanner.Buffer(make([]byte, 4096), 1<<20)
	p := types.Progress{ETA: "unknown"}
	for scanner.Scan() {
		k, v, ok := strings.Cut(scanner.Text(), "=")
		if !ok {
			continue
		}
		v = strings.TrimSpace(v)
		switch k {
		case "frame":
			p.Frame, _ = strconv.ParseInt(v, 10, 64)
		case "fps":
			p.FPS = number(v)
		case "bitrate":
			p.Bitrate = number(strings.TrimSuffix(v, "kbits/s"))
		case "total_size":
			p.TotalSize, _ = strconv.ParseInt(v, 10, 64)
		case "out_time_us":
			p.OutTimeUS, _ = strconv.ParseInt(v, 10, 64)
		case "speed":
			p.Speed = v
		case "progress":
			elapsed := float64(p.OutTimeUS) / 1e6
			if duration > 0 {
				p.Progress = math.Max(0, math.Min(100, elapsed/duration*100))
			}
			p.ETA = "unknown"
			speed := number(strings.TrimSuffix(p.Speed, "x"))
			if speed > 0 && duration > 0 {
				seconds := int64(math.Ceil(math.Max(0, duration-elapsed) / speed))
				p.ETA = fmt.Sprintf("%02d:%02d:%02d", seconds/3600, seconds/60%60, seconds%60)
			}
			emit(p)
		}
	}
	return scanner.Err()
}
func number(v string) float64 {
	n, e := strconv.ParseFloat(strings.TrimSpace(v), 64)
	if e != nil || math.IsNaN(n) || math.IsInf(n, 0) {
		return 0
	}
	return n
}
