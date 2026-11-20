package vmm

import (
	"context"
	"net"
)

type Role string

const (
	RoleChat      Role = "chat"
	RoleTemp      Role = "temp"
	RolePre       Role = "pre"
	RoleInspector Role = "inspector"
)

type Spec struct {
	ID                                   string
	ChatID                               int64
	Role                                 Role
	MemMB, VCPUs, CPUQuotaPercent        int
	ImageVersion, KernelPath, RootfsPath string
	DataDisk                             string
	DataReadOnly                         bool
	TmpMB                                int
	DiskBwMBs, DiskIOPS                  int
}

type Snapshot struct {
	Dir          string
	ImageVersion string
	MemBytes     int64
}

type Backend interface {
	Boot(context.Context, Spec) (Machine, error)
	Restore(context.Context, Spec, Snapshot) (Machine, error)
}

type Machine interface {
	ID() string
	Spec() Spec
	Dial(context.Context) (net.Conn, error)
	Pause(context.Context) error
	Resume(context.Context) error
	SetBalloon(context.Context, int) error
	Snapshot(context.Context, string) (Snapshot, error)
	CPUSeconds() (float64, error)
	Stop(context.Context) error
	Done() <-chan struct{}
}
