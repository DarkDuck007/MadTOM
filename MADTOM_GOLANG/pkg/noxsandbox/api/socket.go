package api

import (
	"context"
	"errors"
	"fmt"
	"net"
	"os"
	"os/user"
	"path/filepath"
	"slices"
	"strconv"
	"time"

	"github.com/DarkDuck007/madtom/pkg/noxsandbox/config"
	"golang.org/x/sys/unix"
)

type peerKey struct{}
type peerConn struct {
	net.Conn
	uid uint32
}
type listener struct {
	*net.UnixListener
	allowed []uint32
}

func Listen(host config.Host) (net.Listener, error) {
	group, err := user.LookupGroup(host.SocketGroup)
	if err != nil {
		return nil, err
	}
	gid, err := strconv.Atoi(group.Gid)
	if err != nil {
		return nil, err
	}
	if err := os.MkdirAll(filepath.Dir(host.SocketPath), 0755); err != nil {
		return nil, err
	}
	if info, err := os.Lstat(host.SocketPath); err == nil {
		if info.Mode()&os.ModeSocket == 0 {
			return nil, fmt.Errorf("API path exists and is not a socket")
		}
		conn, err := net.DialTimeout("unix", host.SocketPath, time.Second)
		if err == nil {
			_ = conn.Close()
			return nil, fmt.Errorf("API socket already active")
		}
		if !errors.Is(err, unix.ECONNREFUSED) {
			return nil, err
		}
		if err := os.Remove(host.SocketPath); err != nil {
			return nil, err
		}
	} else if !os.IsNotExist(err) {
		return nil, err
	}
	unixListener, err := net.ListenUnix("unix", &net.UnixAddr{Name: host.SocketPath, Net: "unix"})
	if err != nil {
		return nil, err
	}
	if err := os.Chown(host.SocketPath, -1, gid); err != nil {
		_ = unixListener.Close()
		return nil, err
	}
	if err := os.Chmod(host.SocketPath, 0660); err != nil {
		_ = unixListener.Close()
		return nil, err
	}
	return &listener{unixListener, slices.Clone(host.AllowedUIDs)}, nil
}

func (listener *listener) Accept() (net.Conn, error) {
	for {
		conn, err := listener.AcceptUnix()
		if err != nil {
			return nil, err
		}
		cred, err := credentials(conn)
		if err == nil && (cred.Uid == 0 || slices.Contains(listener.allowed, cred.Uid)) {
			return &peerConn{conn, cred.Uid}, nil
		}
		_ = conn.Close()
	}
}

func credentials(conn *net.UnixConn) (*unix.Ucred, error) {
	raw, err := conn.SyscallConn()
	if err != nil {
		return nil, err
	}
	var cred *unix.Ucred
	var sockErr error
	if err := raw.Control(func(fd uintptr) { cred, sockErr = unix.GetsockoptUcred(int(fd), unix.SOL_SOCKET, unix.SO_PEERCRED) }); err != nil {
		return nil, err
	}
	return cred, sockErr
}

func ConnContext(ctx context.Context, conn net.Conn) context.Context {
	if conn, ok := conn.(*peerConn); ok {
		return context.WithValue(ctx, peerKey{}, conn.uid)
	}
	return ctx
}
