package types

type Progress struct {
	Frame     int64   `json:"frame"`
	FPS       float64 `json:"fps"`
	Bitrate   float64 `json:"bitrate"` // kbps
	TotalSize int64   `json:"total_size"`
	OutTimeUS int64   `json:"out_time_us"`
	Speed     string  `json:"speed"`
	Progress  float64 `json:"progress"`
	ETA       string  `json:"eta"`
}

type Event struct {
	ID   uint64 `json:"id"`
	Type string `json:"type"`
	Data any    `json:"data"`
}
