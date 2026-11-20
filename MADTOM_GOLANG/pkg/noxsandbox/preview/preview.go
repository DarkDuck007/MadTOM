package preview

import (
	"bytes"
	"fmt"
	"image"
	"image/png"
)

func PNG(width, height, maxPx int, rgba []byte) ([]byte, error) {
	if width <= 0 || height <= 0 || width > maxPx || height > maxPx || width > len(rgba)/4/height || width*height*4 != len(rgba) {
		return nil, fmt.Errorf("invalid raw RGBA dimensions")
	}
	var output bytes.Buffer
	if err := (&png.Encoder{CompressionLevel: png.BestSpeed}).Encode(&output, &image.NRGBA{
		Pix: rgba, Stride: width * 4, Rect: image.Rect(0, 0, width, height),
	}); err != nil {
		return nil, err
	}
	return output.Bytes(), nil
}
