package vmm

import (
	"context"
	"errors"
	"io"
	"os"

	"golang.org/x/sys/unix"
)

func CopySparse(ctx context.Context, source, destination string) (err error) {
	if err := ctx.Err(); err != nil {
		return err
	}
	input, err := os.Open(source)
	if err != nil {
		return err
	}
	defer input.Close()
	info, err := input.Stat()
	if err != nil {
		return err
	}
	output, err := os.OpenFile(destination, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0600)
	if err != nil {
		return err
	}
	defer func() {
		err = errors.Join(err, output.Close())
		if err != nil {
			_ = os.Remove(destination)
		}
	}()
	if unix.IoctlFileClone(int(output.Fd()), int(input.Fd())) == nil {
		return output.Sync()
	}
	buffer := make([]byte, 1<<20)
	for offset := int64(0); offset < info.Size(); {
		if err := ctx.Err(); err != nil {
			return err
		}
		data, err := unix.Seek(int(input.Fd()), offset, unix.SEEK_DATA)
		if errors.Is(err, unix.ENXIO) {
			break
		}
		end := info.Size()
		if err == nil {
			end, err = unix.Seek(int(input.Fd()), data, unix.SEEK_HOLE)
		} else if errors.Is(err, unix.EINVAL) || errors.Is(err, unix.EOPNOTSUPP) {
			data, err = offset, nil
		}
		if err != nil {
			return err
		}
		for offset = data; offset < end; {
			if err := ctx.Err(); err != nil {
				return err
			}
			chunk := buffer[:min(int64(len(buffer)), end-offset)]
			if _, err := input.ReadAt(chunk, offset); err != nil {
				return err
			}
			if !allZero(chunk) {
				if wrote, err := output.WriteAt(chunk, offset); err != nil {
					return err
				} else if wrote != len(chunk) {
					return io.ErrShortWrite
				}
			}
			offset += int64(len(chunk))
		}
	}
	if err := output.Truncate(info.Size()); err != nil {
		return err
	}
	return output.Sync()
}

func allZero(data []byte) bool {
	for _, value := range data {
		if value != 0 {
			return false
		}
	}
	return true
}

func PunchZeroPages(ctx context.Context, file *os.File) error {
	page := os.Getpagesize()
	buffer := make([]byte, 256*page)
	var offset, zeros int64
	punch := func(end int64) error {
		if zeros == 0 {
			return nil
		}
		err := unix.Fallocate(int(file.Fd()), unix.FALLOC_FL_PUNCH_HOLE|unix.FALLOC_FL_KEEP_SIZE, end-zeros, zeros)
		zeros = 0
		return err
	}
	for {
		if err := ctx.Err(); err != nil {
			return err
		}
		size, err := io.ReadFull(file, buffer)
		for index := 0; index+page <= size; index += page {
			if allZero(buffer[index : index+page]) {
				zeros += int64(page)
			} else if punchErr := punch(offset + int64(index)); punchErr != nil {
				return punchErr
			}
		}
		offset += int64(size / page * page)
		if err == io.EOF || err == io.ErrUnexpectedEOF {
			return punch(offset)
		}
		if err != nil {
			return err
		}
	}
}
