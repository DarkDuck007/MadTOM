package ffmpeg

import (
	"fmt"
	"regexp"
	"github.com/DarkDuck007/madtom/pkg/squeeze/types"
	"strconv"
	"strings"
)

var denoisePattern = regexp.MustCompile(`^hqdn3d(=([0-9]+(\.[0-9]+)?)(:[0-9]+(\.[0-9]+)?){0,3})?$`)

func Normalize(s types.JobSpec) (types.JobSpec, error) {
	if s.VideoCodec == "" {
		s.VideoCodec = "libx265"
	}
	if s.Container == "" {
		s.Container = "mp4"
	}
	if s.AudioCodec == "" {
		s.AudioCodec = "aac"
	}
	if s.CRF == nil {
		v := 23
		s.CRF = &v
	} else {
		v := *s.CRF
		s.CRF = &v
	}
	if !contains([]string{"libx264", "libx265", "libsvtav1", "libaom-av1", "libvpx-vp9", "h264_nvenc", "hevc_nvenc", "av1_nvenc", "h264_qsv", "hevc_qsv", "av1_qsv", "h264_vaapi", "hevc_vaapi", "av1_vaapi"}, s.VideoCodec) {
		return s, fmt.Errorf("unsupported video_codec")
	}
	if !contains([]string{"mp4", "mkv", "webm"}, s.Container) {
		return s, fmt.Errorf("unsupported container")
	}
	if !contains([]string{"aac", "libopus", "copy", "none"}, s.AudioCodec) {
		return s, fmt.Errorf("unsupported audio_codec")
	}
	if s.Container == "webm" && (!strings.Contains(s.VideoCodec, "av1") && s.VideoCodec != "libvpx-vp9" || s.AudioCodec != "libopus" && s.AudioCodec != "none") {
		return s, fmt.Errorf("webm requires VP9/AV1 and libopus/none audio")
	}
	if *s.CRF < 0 || *s.CRF > 51 {
		return s, fmt.Errorf("crf must be 0..51")
	}
	if s.Width < 0 || s.Height < 0 || s.Width > 16384 || s.Height > 16384 || s.Width%2 != 0 || s.Height%2 != 0 {
		return s, fmt.Errorf("dimensions must be even numbers from 0..16384")
	}
	if s.FPS < 0 || s.FPS > 240 {
		return s, fmt.Errorf("fps must be 0..240")
	}
	if s.AudioBitrate < 0 || s.AudioBitrate > 512 {
		return s, fmt.Errorf("audio_bitrate must be 0..512 kbps")
	}
	if s.AudioBitrate > 0 && (s.AudioCodec == "none" || s.AudioCodec == "copy") {
		return s, fmt.Errorf("audio_bitrate requires an audio encoder")
	}
	if s.Denoise != "" && !denoisePattern.MatchString(s.Denoise) {
		return s, fmt.Errorf("denoise must be hqdn3d with up to four nonnegative numeric strengths")
	}
	if s.AudioChannels < 0 || s.AudioChannels > 8 {
		return s, fmt.Errorf("audio_channels must be 0..8")
	}
	if s.Tune != "" && (s.VideoCodec != "libx264" && s.VideoCodec != "libx265" || !contains([]string{"film", "animation", "grain", "stillimage", "fastdecode", "zerolatency", "psnr", "ssim"}, s.Tune)) {
		return s, fmt.Errorf("unsupported tune for video_codec")
	}
	if s.CropTop < 0 || s.CropBottom < 0 || s.CropLeft < 0 || s.CropRight < 0 ||
		s.CropTop > 4096 || s.CropBottom > 4096 || s.CropLeft > 4096 || s.CropRight > 4096 {
		return s, fmt.Errorf("crop offsets must be 0..4096")
	}
	presets := []string{}
	switch {
	case s.VideoCodec == "libx264" || s.VideoCodec == "libx265":
		presets = []string{"ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"}
		if s.Preset == "" {
			s.Preset = "medium"
		}
	case strings.HasSuffix(s.VideoCodec, "_nvenc"):
		presets = []string{"p1", "p2", "p3", "p4", "p5", "p6", "p7"}
		if s.Preset == "" {
			s.Preset = "p4"
		}
	case strings.HasSuffix(s.VideoCodec, "_qsv"):
		presets = []string{"veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"}
		if s.Preset == "" {
			s.Preset = "medium"
		}
	case s.VideoCodec == "libsvtav1":
		for i := 0; i <= 13; i++ {
			presets = append(presets, strconv.Itoa(i))
		}
		if s.Preset == "" {
			s.Preset = "8"
		}
	case s.VideoCodec == "libaom-av1" || s.VideoCodec == "libvpx-vp9":
		for i := 0; i <= 8; i++ {
			presets = append(presets, strconv.Itoa(i))
		}
		if s.Preset == "" {
			s.Preset = "4"
		}
	}
	if s.Preset != "" && !contains(presets, s.Preset) {
		return s, fmt.Errorf("invalid preset for %s", s.VideoCodec)
	}
	return s, nil
}
func contains(values []string, value string) bool {
	for _, v := range values {
		if v == value {
			return true
		}
	}
	return false
}

func Build(s types.JobSpec, input, output string) ([]string, error) {
	s, err := Normalize(s)
	if err != nil {
		return nil, err
	}
	args := []string{"-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-nostats", "-v", "error"}
	if strings.HasSuffix(s.VideoCodec, "_vaapi") {
		args = append(args, "-vaapi_device", "/dev/dri/renderD128")
	}
	args = append(args, "-protocol_whitelist", "file,pipe", "-i", input, "-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn", "-c:v", s.VideoCodec)
	quality := strconv.Itoa(*s.CRF)
	switch {
	case strings.HasSuffix(s.VideoCodec, "_nvenc"):
		args = append(args, "-rc", "vbr", "-cq", quality, "-b:v", "0")
	case strings.HasSuffix(s.VideoCodec, "_qsv"), strings.HasSuffix(s.VideoCodec, "_vaapi"):
		args = append(args, "-global_quality", quality)
	default:
		args = append(args, "-crf", quality)
		if s.VideoCodec == "libvpx-vp9" || s.VideoCodec == "libaom-av1" {
			args = append(args, "-b:v", "0")
		}
	}
	if s.Preset != "" {
		flag := "-preset"
		if s.VideoCodec == "libaom-av1" || s.VideoCodec == "libvpx-vp9" {
			flag = "-cpu-used"
		}
		args = append(args, flag, s.Preset)
	}
	if s.Tune != "" {
		args = append(args, "-tune", s.Tune)
	}
	filters := []string{}
	if s.CropTop > 0 || s.CropBottom > 0 || s.CropLeft > 0 || s.CropRight > 0 {
		filters = append(filters, fmt.Sprintf("crop=iw-%d:ih-%d:%d:%d", s.CropLeft+s.CropRight, s.CropTop+s.CropBottom, s.CropLeft, s.CropTop))
	}
	if s.Deinterlace {
		filters = append(filters, "yadif")
	}
	if s.Denoise != "" {
		filters = append(filters, s.Denoise)
	}
	if s.Width > 0 || s.Height > 0 {
		w, h := s.Width, s.Height
		if w == 0 {
			w = -2
		}
		if h == 0 {
			h = -2
		}
		filters = append(filters, fmt.Sprintf("scale=%d:%d", w, h))
	}
	if s.Grayscale {
		filters = append(filters, "hue=s=0")
	}
	if s.FPS > 0 {
		filters = append(filters, "fps="+strconv.FormatFloat(s.FPS, 'f', -1, 64))
	}
	if strings.HasSuffix(s.VideoCodec, "_vaapi") {
		filters = append(filters, "format=nv12", "hwupload")
	} else {
		pixFmt := "yuv420p"
		if s.PixelFormat != "" && s.PixelFormat != "auto" {
			pixFmt = s.PixelFormat
		}
		args = append(args, "-pix_fmt", pixFmt)
	}
	if len(filters) > 0 {
		args = append(args, "-vf", strings.Join(filters, ","))
	}
	if s.AudioCodec == "none" {
		args = append(args, "-an")
	} else {
		args = append(args, "-c:a", s.AudioCodec)
		if s.AudioBitrate > 0 {
			args = append(args, "-b:a", strconv.Itoa(s.AudioBitrate)+"k")
		}
		if s.AudioChannels > 0 {
			args = append(args, "-ac", strconv.Itoa(s.AudioChannels))
		}
	}
	format := s.Container
	if format == "mkv" {
		format = "matroska"
	}
	if format == "mp4" {
		args = append(args, "-movflags", "+faststart")
	}
	return append(args, "-f", format, output), nil
}
