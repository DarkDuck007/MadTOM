//go:build !unix

package ffmpeg

import (
	"fmt"
	"os"
)

const PauseSupported = false

func pause(p *os.Process) error     { return fmt.Errorf("pause is unsupported on this operating system") }
func resume(p *os.Process) error    { return fmt.Errorf("resume is unsupported on this operating system") }
func interrupt(p *os.Process) error { return p.Kill() }
