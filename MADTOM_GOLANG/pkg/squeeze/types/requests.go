package types

// CreateJobRequest fixes the upload size before accepting any media bytes.
type CreateJobRequest struct {
	Filename  string  `json:"filename"`
	FileSize  int64   `json:"file_size"`
	SHA256    string  `json:"sha256,omitempty"`
	PresetKey string  `json:"preset_key,omitempty"`
	Spec      JobSpec `json:"spec"`
}

// JobSpec deliberately exposes structured options, never arbitrary CLI arguments.
type JobSpec struct {
	VideoCodec    string  `json:"video_codec" yaml:"video_codec"`
	CRF           *int    `json:"crf,omitempty" yaml:"crf,omitempty"`
	Preset        string  `json:"preset,omitempty" yaml:"preset,omitempty"`
	Container     string  `json:"container" yaml:"container"`
	AudioCodec    string  `json:"audio_codec" yaml:"audio_codec"`
	AudioBitrate  int     `json:"audio_bitrate,omitempty" yaml:"audio_bitrate,omitempty"` // kbps
	AudioChannels int     `json:"audio_channels,omitempty" yaml:"audio_channels,omitempty"`
	Tune          string  `json:"tune,omitempty" yaml:"tune,omitempty"`
	Denoise       string  `json:"denoise,omitempty" yaml:"denoise,omitempty"`
	Width         int     `json:"width,omitempty" yaml:"width,omitempty"`
	Height        int     `json:"height,omitempty" yaml:"height,omitempty"`
	FPS           float64 `json:"fps,omitempty" yaml:"fps,omitempty"`
	Deinterlace   bool    `json:"deinterlace,omitempty" yaml:"deinterlace,omitempty"`
	Grayscale     bool    `json:"grayscale,omitempty" yaml:"grayscale,omitempty"`
	PixelFormat   string  `json:"pixel_format,omitempty" yaml:"pixel_format,omitempty"`
	CropTop       int     `json:"crop_top,omitempty" yaml:"crop_top,omitempty"`
	CropBottom    int     `json:"crop_bottom,omitempty" yaml:"crop_bottom,omitempty"`
	CropLeft      int     `json:"crop_left,omitempty" yaml:"crop_left,omitempty"`
	CropRight     int     `json:"crop_right,omitempty" yaml:"crop_right,omitempty"`
}
