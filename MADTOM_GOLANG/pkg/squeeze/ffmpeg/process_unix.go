//go:build unix

package ffmpeg

import (
	"os"
	"syscall"
)

const PauseSupported = true

func pause(p *os.Process) error     { return p.Signal(syscall.SIGSTOP) }
func resume(p *os.Process) error    { return p.Signal(syscall.SIGCONT) }
func interrupt(p *os.Process) error { return p.Signal(os.Interrupt) }
