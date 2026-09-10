package downsample

import (
	"math"

	"github.com/DarkDuck007/madtom/pkg/collector/storage"
)

// DownsampleLTTB downsamples an array of time-series points to targetPoints using Largest-Triangle-Three-Buckets.
func DownsampleLTTB(data []storage.Point, targetPoints int) []storage.Point {
	dataLen := len(data)
	if targetPoints >= dataLen || targetPoints <= 2 {
		return data
	}

	sampled := make([]storage.Point, 0, targetPoints)
	// Always include the very first point
	sampled = append(sampled, data[0])

	bucketSize := float64(dataLen-2) / float64(targetPoints-2)
	aIdx := 0

	for i := 0; i < targetPoints-2; i++ {
		// Calculate range of current bucket
		bucketStart := int(math.Floor(float64(i)*bucketSize)) + 1
		bucketEnd := int(math.Floor(float64(i+1)*bucketSize)) + 1
		if bucketEnd > dataLen {
			bucketEnd = dataLen
		}

		// Calculate range and center of next bucket (c)
		nextBucketStart := int(math.Floor(float64(i+1)*bucketSize)) + 1
		nextBucketEnd := int(math.Floor(float64(i+2)*bucketSize)) + 1
		if nextBucketEnd > dataLen {
			nextBucketEnd = dataLen
		}

		var avgX, avgY float64
		nextBucketCount := float64(nextBucketEnd - nextBucketStart)
		if nextBucketCount > 0 {
			for j := nextBucketStart; j < nextBucketEnd; j++ {
				avgX += float64(data[j].TimestampUnixNano)
				avgY += data[j].Value
			}
			avgX /= nextBucketCount
			avgY /= nextBucketCount
		} else if nextBucketStart < dataLen {
			avgX = float64(data[nextBucketStart].TimestampUnixNano)
			avgY = data[nextBucketStart].Value
		}

		// Point a
		pointAX := float64(data[aIdx].TimestampUnixNano)
		pointAY := data[aIdx].Value

		maxArea := -1.0
		maxIdx := bucketStart

		for j := bucketStart; j < bucketEnd; j++ {
			// Triangle area = 0.5 * |(Ax - Cx)(By - Ay) - (Ax - Bx)(Cy - Ay)|
			area := math.Abs((pointAX-avgX)*(data[j].Value-pointAY)-(pointAX-float64(data[j].TimestampUnixNano))*(avgY-pointAY)) * 0.5
			if area > maxArea {
				maxArea = area
				maxIdx = j
			}
		}

		sampled = append(sampled, data[maxIdx])
		aIdx = maxIdx
	}

	// Always include the very last point
	sampled = append(sampled, data[dataLen-1])
	return sampled
}
