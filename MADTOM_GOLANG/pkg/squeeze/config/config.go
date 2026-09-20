package config

import (
	"flag"
	"fmt"
	"net"
	"os"
	"path/filepath"
	"strconv"
	"time"

	"github.com/DarkDuck007/madtom/pkg/squeeze/types"

	"gopkg.in/yaml.v3"
)

const Version = "1.0.0"

type Config struct {
	ConfigFile    string
	Host          string
	Port          int
	Scratch       string
	Output        string
	FFmpeg        string
	FFprobe       string
	NodeID        string
	Workers       int
	QueueSize     int
	MaxUpload     int64
	Retention     time.Duration
	UploadTimeout time.Duration
	MDNS          bool
	Token         string
	Presets       []types.Preset
}

type yamlFile struct {
	Server struct {
		Host          *string        `yaml:"host"`
		Port          *int           `yaml:"port"`
		Scratch       *string        `yaml:"scratch"`
		Output        *string        `yaml:"output"`
		FFmpeg        *string        `yaml:"ffmpeg"`
		FFprobe       *string        `yaml:"ffprobe"`
		NodeID        *string        `yaml:"node_id"`
		Workers       *int           `yaml:"workers"`
		QueueSize     *int           `yaml:"queue_size"`
		MaxUpload     *int64         `yaml:"max_upload"`
		Retention     *time.Duration `yaml:"retention"`
		UploadTimeout *time.Duration `yaml:"upload_timeout"`
		MDNS          *bool          `yaml:"mdns"`
		Token         *string        `yaml:"token"`
	} `yaml:"server"`
	Presets []types.Preset `yaml:"presets"`
}

func Parse(args []string) (Config, error) {
	c := Config{}
	env := func(k, fallback string) string {
		if v, ok := os.LookupEnv("SQUEEZE_" + k); ok {
			return v
		}
		return fallback
	}

	// 1. First pass: find --config or -c flag to load YAML settings
	var configFile string
	configEnv := env("CONFIG", "")
	for i, arg := range args {
		if arg == "--config" || arg == "-c" {
			if i+1 < len(args) {
				configFile = args[i+1]
			}
		} else if len(arg) > 9 && arg[:9] == "--config=" {
			configFile = arg[9:]
		} else if len(arg) > 3 && arg[:3] == "-c=" {
			configFile = arg[3:]
		}
	}
	if configFile == "" {
		configFile = configEnv
	}
	c.ConfigFile = configFile

	var y yamlFile
	if configFile != "" {
		data, err := os.ReadFile(configFile)
		if err != nil {
			return c, fmt.Errorf("reading config file %q: %w", configFile, err)
		}
		if err := yaml.Unmarshal(data, &y); err != nil {
			return c, fmt.Errorf("parsing YAML config file %q: %w", configFile, err)
		}
		c.Presets = y.Presets
	}

	// 2. Base defaults
	hostname, _ := os.Hostname()
	defaultHost := "0.0.0.0"
	defaultPort := 8080
	defaultScratch := filepath.Join(os.TempDir(), "squeeze", "scratch")
	defaultOutput := filepath.Join(os.TempDir(), "squeeze", "output")
	defaultFFmpeg := "ffmpeg"
	defaultFFprobe := "ffprobe"
	defaultNodeID := hostname
	defaultWorkers := 1
	defaultQueueSize := 128
	defaultMaxUpload := int64(10 << 30)
	defaultRetention := 24 * time.Hour
	defaultUploadTimeout := 30 * time.Minute
	defaultMDNS := true
	defaultToken := ""

	// Apply YAML defaults if provided
	if y.Server.Host != nil {
		defaultHost = *y.Server.Host
	}
	if y.Server.Port != nil {
		defaultPort = *y.Server.Port
	}
	if y.Server.Scratch != nil {
		defaultScratch = *y.Server.Scratch
	}
	if y.Server.Output != nil {
		defaultOutput = *y.Server.Output
	}
	if y.Server.FFmpeg != nil {
		defaultFFmpeg = *y.Server.FFmpeg
	}
	if y.Server.FFprobe != nil {
		defaultFFprobe = *y.Server.FFprobe
	}
	if y.Server.NodeID != nil {
		defaultNodeID = *y.Server.NodeID
	}
	if y.Server.Workers != nil {
		defaultWorkers = *y.Server.Workers
	}
	if y.Server.QueueSize != nil {
		defaultQueueSize = *y.Server.QueueSize
	}
	if y.Server.MaxUpload != nil {
		defaultMaxUpload = *y.Server.MaxUpload
	}
	if y.Server.Retention != nil {
		defaultRetention = *y.Server.Retention
	}
	if y.Server.UploadTimeout != nil {
		defaultUploadTimeout = *y.Server.UploadTimeout
	}
	if y.Server.MDNS != nil {
		defaultMDNS = *y.Server.MDNS
	}
	if y.Server.Token != nil {
		defaultToken = *y.Server.Token
	}

	// 3. Flags definition with initial values derived from YAML and ENV
	f := flag.NewFlagSet("squeeze-server", flag.ContinueOnError)
	f.StringVar(&c.ConfigFile, "config", configFile, "path to YAML configuration file")
	f.StringVar(&c.ConfigFile, "c", configFile, "path to YAML configuration file (shorthand)")
	f.StringVar(&c.Host, "host", env("HOST", defaultHost), "HTTP bind address")
	f.StringVar(&c.Scratch, "scratch", env("SCRATCH", defaultScratch), "dedicated scratch directory")
	f.StringVar(&c.Output, "output", env("OUTPUT", defaultOutput), "dedicated output directory")
	f.StringVar(&c.FFmpeg, "ffmpeg", env("FFMPEG", defaultFFmpeg), "ffmpeg executable")
	f.StringVar(&c.FFprobe, "ffprobe", env("FFPROBE", defaultFFprobe), "ffprobe executable")
	f.StringVar(&c.NodeID, "node-id", env("NODE_ID", defaultNodeID), "discovery node ID")
	f.StringVar(&c.Token, "token", env("TOKEN", defaultToken), "optional bearer token")
	f.IntVar(&c.Port, "port", defaultPort, "HTTP port")
	f.IntVar(&c.Workers, "workers", defaultWorkers, "concurrent encodes")
	f.IntVar(&c.QueueSize, "queue-size", defaultQueueSize, "maximum waiting encodes")
	f.Int64Var(&c.MaxUpload, "max-upload", defaultMaxUpload, "maximum upload bytes")
	f.DurationVar(&c.Retention, "retention", defaultRetention, "inactive upload and terminal job retention")
	f.DurationVar(&c.UploadTimeout, "upload-timeout", defaultUploadTimeout, "maximum duration of one upload request")
	f.BoolVar(&c.MDNS, "mdns", defaultMDNS, "advertise via mDNS")

	for k, n := range map[string]string{
		"PORT": "port", "WORKERS": "workers", "QUEUE_SIZE": "queue-size",
		"MAX_UPLOAD": "max-upload", "RETENTION": "retention",
		"UPLOAD_TIMEOUT": "upload-timeout", "MDNS": "mdns",
	} {
		if v, ok := os.LookupEnv("SQUEEZE_" + k); ok {
			if err := f.Set(n, v); err != nil {
				return c, fmt.Errorf("SQUEEZE_%s: %w", k, err)
			}
		}
	}

	if err := f.Parse(args); err != nil {
		return c, err
	}
	if f.NArg() != 0 {
		return c, fmt.Errorf("unexpected positional arguments")
	}
	if c.Port < 1 || c.Port > 65535 || c.Workers < 1 || c.QueueSize < 1 || c.MaxUpload < 1 || c.Retention <= 0 || c.UploadTimeout <= 0 {
		return c, fmt.Errorf("invalid port, limits, or durations")
	}
	if c.NodeID == "" {
		return c, fmt.Errorf("node-id cannot be empty")
	}
	return c, nil
}

func (c Config) Address() string { return net.JoinHostPort(c.Host, strconv.Itoa(c.Port)) }
