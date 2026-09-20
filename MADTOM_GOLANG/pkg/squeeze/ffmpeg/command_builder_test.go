package ffmpeg

import (
	"github.com/DarkDuck007/madtom/pkg/squeeze/types"
	"strings"
	"testing"
)

func TestBuild(t *testing.T) {
	q := 24
	for _, tc := range []struct{ codec, preset, want string }{
		{"libx265", "slow", "-crf 24 -preset slow"},
		{"h264_nvenc", "p5", "-rc vbr -cq 24 -b:v 0 -preset p5"},
		{"hevc_qsv", "fast", "-global_quality 24 -preset fast"},
		{"libsvtav1", "8", "-crf 24 -preset 8"},
		{"libvpx-vp9", "4", "-crf 24 -b:v 0 -cpu-used 4"},
		{"hevc_vaapi", "", "-global_quality 24"},
	} {
		t.Run(tc.codec, func(t *testing.T) {
			args, err := Build(types.JobSpec{VideoCodec: tc.codec, CRF: &q, Preset: tc.preset, Width: 1280, Deinterlace: true, Grayscale: true, AudioBitrate: 128}, "/input with spaces", "/output.tmp")
			if err != nil {
				t.Fatal(err)
			}
			joined := strings.Join(args, " ")
			for _, want := range []string{tc.want, "-progress pipe:1", "yadif,scale=1280:-2,hue=s=0", "-b:a 128k", "-f mp4 /output.tmp"} {
				if !strings.Contains(joined, want) {
					t.Errorf("missing %q in %q", want, joined)
				}
			}
		})
	}
}
func TestRejectInvalidSpec(t *testing.T) {
	for _, s := range []types.JobSpec{{Denoise: "hqdn3d,movie=/tmp/input"}, {AudioChannels: -1}, {Tune: "invalid"}, {VideoCodec: "-i evil"}, {Preset: "slow;touch /tmp/foo"}, {Width: 123}, {AudioCodec: "flac"}, {Container: "../mp4"}, {Container: "webm"}, {VideoCodec: "libsvtav1", Preset: "slow"}, {AudioCodec: "copy", AudioBitrate: 128}} {
		if _, err := Normalize(s); err == nil {
			t.Errorf("accepted %+v", s)
		}
	}
}
