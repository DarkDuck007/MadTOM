package types

import "time"

type JobDetail struct {
	JobID        string    `json:"job_id"`
	Status       string    `json:"status"`
	Filename     string    `json:"filename"`
	FileSize     int64     `json:"file_size"`
	UploadOffset int64     `json:"upload_offset"`
	SHA256       string    `json:"sha256,omitempty"`
	Spec         JobSpec   `json:"spec"`
	Progress     Progress  `json:"progress"`
	Error        string    `json:"error,omitempty"`
	CreatedAt    time.Time `json:"created_at"`
	UpdatedAt    time.Time `json:"updated_at"`
	UploadURL    string    `json:"upload_url"`
	EventsURL    string    `json:"events_url"`
	DownloadURL  string    `json:"download_url,omitempty"`
}

type Health struct {
	Status               string         `json:"status"`
	Version              string         `json:"version"`
	NodeID               string         `json:"node_id"`
	UptimeSeconds        float64        `json:"uptime_seconds"`
	CPUCores             int            `json:"cpu_cores"`
	LoadAverage          *float64       `json:"load_average,omitempty"`
	ProcessMemoryBytes   uint64         `json:"process_memory_bytes"`
	SystemMemoryBytes    uint64         `json:"system_memory_bytes,omitempty"`
	AvailableMemoryBytes uint64         `json:"available_memory_bytes,omitempty"`
	MaxConcurrent        int            `json:"max_concurrent"`
	Jobs                 map[string]int `json:"jobs"`
	Encoders             []string       `json:"encoders"`
	HardwareEncoders     []string       `json:"hardware_encoders"`
	HardwareStatus       string         `json:"hardware_status"`
	PauseSupported       bool           `json:"pause_supported"`
}
