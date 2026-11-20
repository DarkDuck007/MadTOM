package manager

import (
	"bytes"
	"fmt"
	"image"
	"image/jpeg"
	"image/png"
	"path"
	"strings"
)

// Exported stills are decoded with Go's memory-safe codecs and re-encoded before they can reach
// NoxChat's Pillow-based image store (contracts §2.1). Everything else stays opaque bytes.
const (
	maxSanitizeSide   = 8192
	maxSanitizePixels = 40_000_000
)

func sanitizableFormat(name string) string {
	switch strings.ToLower(path.Ext(name)) {
	case ".png":
		return "png"
	case ".jpg", ".jpeg":
		return "jpeg"
	}
	return ""
}

func sanitizeImage(data []byte, format string) ([]byte, error) {
	cfg, kind, err := image.DecodeConfig(bytes.NewReader(data))
	if err != nil {
		return nil, err
	}
	if kind != format {
		return nil, fmt.Errorf("content is %s, extension says %s", kind, format)
	}
	if cfg.Width <= 0 || cfg.Height <= 0 || cfg.Width > maxSanitizeSide || cfg.Height > maxSanitizeSide ||
		int64(cfg.Width)*int64(cfg.Height) > maxSanitizePixels {
		return nil, fmt.Errorf("image dimensions %dx%d exceed limits", cfg.Width, cfg.Height)
	}
	img, _, err := image.Decode(bytes.NewReader(data))
	if err != nil {
		return nil, err
	}
	var out bytes.Buffer
	if format == "png" {
		err = (&png.Encoder{CompressionLevel: png.DefaultCompression}).Encode(&out, img)
	} else {
		err = jpeg.Encode(&out, img, &jpeg.Options{Quality: 90})
	}
	if err != nil {
		return nil, err
	}
	return out.Bytes(), nil
}
