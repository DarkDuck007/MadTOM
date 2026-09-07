# Mad TOM (Multiplatform Advanced Device Telemetry and Operation Monitoring)
## System Architecture, IPC Design, and Telemetry Engine Specification

---

## 1. Executive Summary & Project Identity

### 1.1 The Backronym & Vision
**Mad TOM** stands for **Multiplatform Advanced Device Telemetry and Operation Monitoring**. It was conceptualized to resolve the conflict between two software engineering archetypes:
1. **The Irreverent Homelab Engineer:** Demands playful, memorable, and thematic mental models (geese, cats, honks, purrs).
2. **The Professional Enterprise Architect:** Requires strict, unambiguous, professional terminology for compliance, documentation, executive demos, and open-source portfolio presentation.

By decoupling the internal event bus from the visual presentation layer through a dynamic **Lexicon Engine**, Mad TOM operates simultaneously as a battle-hardened infrastructure observability platform and an unapologetic goose-farming simulator.

### 1.2 Legal & Trademark Clearance
Early naming candidates encountered domain and trademark collisions:
* **Gaggle:** Pre-empted by *Gaggle Inc.*, a major player in student safety, content filtering, and educational telemetry. Commercialization or public release under this name posed immediate trademark infringement liabilities.
* **Litterbox / CatBox:** Suffered from negative psychological framing (debugging network latency inside cat excrement during a critical production outage) and generic collision with container sandboxing utilities.
* **Skein / Gander / Anser:** Valid ornithological options, but lacked the direct, punchy brand utility of a centralized operations suite.
* **Mad TOM:** Clear trademark space across systems monitoring, distinct executable names (`madtomd`, `madtomctl`, `madtom-gui`), and carries an aerospace-grade retro backronym.

---

## 2. Dynamic Lexicon & Persona Engine

### 2.1 The Concept
The telemetry core does not manipulate localized strings. It emits strict, strongly typed protocol enumerations and standard metric primitives (e.g., `DEVICE_TYPE_BARE_METAL`, `STATUS_DEGRADED`, `PACKET_LOSS_EXCEEDED`).

The UI host (Avalonia C#) consumes an `ILexiconService` that maps these primitives to user-selectable language/persona packs loaded dynamically at runtime via JSON. Swapping themes forces an in-place visual re-evaluation without restarting the application or destroying viewport states.

### 2.2 Persona Mapping Matrix

| Internal Semantic Key | Standard (Enterprise) | Goose (Homelab Default)   | Feline (Chaos Mode) | Sci-Fi / Cyberpunk   |
| :-------------------- | :-------------------- | :------------------------ | :------------------ | :------------------- |
| `Node.BareMetal`      | Dedicated Host        | Gander                    | Tomcat              | Mainframe Core       |
| `Node.Virtual`        | Virtual Instance      | Gosling                   | Kitten              | Sub-Routine Node     |
| `Node.Container`      | Container / Pod       | Egg                       | Mouse               | Nanite Shard         |
| `Telemetry.Ping`      | Heartbeat (ICMP/UDP)  | Honk                      | Meow / Purr         | Sub-Space Pulse      |
| `Telemetry.TWAMP`     | TWAMP Metric Probe    | Flight Formation Survey   | Pounce Calculation  | Quantum Drift Check  |
| `Alert.Warning`       | Threshold Degraded    | Defensive Hiss            | Low Growl           | Hull Stress Warning  |
| `Alert.Critical`      | System Unreachable    | Violent Peck              | Unhinged Hiss       | Core Breach Imminent |
| `Action.Silence`      | Mute Notifications    | Scatter Corn / Bread Toss | Toss Laser Pointer  | Dampen Dampeners     |
| `Fleet.Cluster`       | Cluster / Subnet      | The Pond                  | The Clowder         | Grid Sector          |
| `Daemon.Process`      | `madtom-agent`        | `goosed`                  | `stray-cat`         | `daemon-runner`      |

---

### 2.3 Avalonia UI Implementation (C#)

The localization system uses Avalonia's dependency injection and XAML binding pipeline. It relies on an indexer property (`this[string key]`) that raises an `Item[]` change notification, instructing Avalonia’s layout engine to invalidate all active string bindings.

#### Directory Layout
```
MadTom.Client/
├── Assets/
│   └── Lexicons/
│       ├── standard.json
│       ├── goose.json
│       └── feline.json
├── Localization/
│   ├── LexiconService.cs
│   └── LocExtension.cs
└── Views/
    └── HostCardView.axaml
```

#### JSON Definitions
`Assets/Lexicons/standard.json`
```json
{
  "Node.BareMetal": "Dedicated Server",
  "Node.Virtual": "Virtual Instance",
  "Telemetry.Ping": "Heartbeat",
  "Telemetry.TWAMP": "TWAMP Latency Test",
  "Alert.Warning": "Degraded Performance",
  "Alert.Critical": "Node Unresponsive"
}
```

`Assets/Lexicons/goose.json`
```json
{
  "Node.BareMetal": "Gander",
  "Node.Virtual": "Gosling",
  "Telemetry.Ping": "Honk",
  "Telemetry.TWAMP": "V-Formation Drift",
  "Alert.Warning": "Defensive Hiss",
  "Alert.Critical": "Violent Peck"
}
```

#### The Observable Lexicon Engine (`LexiconService.cs`)
```csharp
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Platform;

namespace MadTom.Client.Localization;

public sealed class LexiconService : INotifyPropertyChanged
{
    private static readonly Lazy<LexiconService> _lazyInstance = new(() => new LexiconService());
    public static LexiconService Instance => _lazyInstance.Value;

    private Dictionary<string, string> _activeTokens = new(StringComparer.OrdinalIgnoreCase);
    public string CurrentPack { get; private set; } = "standard";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key]
    {
        get
        {
            if (_activeTokens.TryGetValue(key, out var translation))
            {
                return translation;
            }
            return $"!{key}!"; // Missing key sentinel
        }
    }

    private LexiconService()
    {
        LoadLexicon("goose"); // Default persona
    }

    public void LoadLexicon(string packName)
    {
        var uri = new Uri($"avares://MadTom.Client/Assets/Lexicons/{packName.ToLowerInvariant()}.json");
        
        if (!AssetLoader.Exists(uri))
        {
            throw new FileNotFoundException($"Lexicon asset not found: {uri}");
        }

        using var stream = AssetLoader.Open(uri);
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        if (parsed != null)
        {
            _activeTokens = parsed;
            CurrentPack = packName;
            
            // "Item[]" forces the binding engine to invalidate all indexer lookups
            OnPropertyChanged("Item[]");
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
```

#### Markup Extension (`LocExtension.cs`)
```csharp
using System;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace MadTom.Client.Localization;

public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; }

    public LocExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LexiconService.Instance,
            Mode = BindingMode.OneWay
        };

        return binding.ProvideValue(serviceProvider);
    }
}
```

#### XAML View Integration (`HostCardView.axaml`)
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:loc="using:MadTom.Client.Localization"
             x:Class="MadTom.Client.Views.HostCardView">
    <Border Classes="card" CornerRadius="6" Padding="12">
        <StackPanel Spacing="6">
            <!-- Evaluates to "Dedicated Server" or "Gander" dynamically -->
            <TextBlock Text="{loc:Loc Node.BareMetal}" 
                       FontWeight="SemiBold" 
                       Foreground="{DynamicResource AccentBrush}"/>

            <!-- Evaluates to "Heartbeat" or "Honk" dynamically -->
            <Button Content="{loc:Loc Telemetry.Ping}" 
                    Command="{Binding SendPingCommand}"
                    HorizontalAlignment="Right"/>
        </StackPanel>
    </Border>
</UserControl>
```

---

## 3. The Backend Communication Problem

### 3.1 The Technical Requirements
1. **Deep Network Telemetry:** Implement two-way active measurement protocols (TWAMP - RFC 5357), high-precision ICMP jitter estimation, hardware-level interface packet captures, and routing table tracking.
2. **Ecosystem Reality:** Go features mature, production-proven networking implementations (such as `github.com/tcaine/twamp` or raw socket libraries). .NET lacks native, battle-tested implementations of TWAMP; building an RFC-compliant TWAMP control/test engine from scratch in C# represents high technical debt.
3. **Cross-Platform Delivery:** Must operate uniformly on Linux (systemd, raw capabilities), Windows, and macOS.

### 3.2 Evaluation of Inter-Runtime Paradigms

```
+-------------------------------------------------------------+
|                      Evaluated Approaches                   |
+------------------------------+------------------------------+
| [A] In-Process Interop       | [B] Out-of-Process IPC       |
| P/Invoke via CGO / c-shared  | Localhost gRPC / Unix Socket |
+------------------------------+------------------------------+
| - Shared memory space        | - Process isolation boundary |
| - High cross-compiler burden | - Clean cross-compilation    |
| - Permission inheritance     | - Strict capability splitting|
| - Runtime signal collision   | - Fault tolerance & recovery |
+------------------------------+------------------------------+
```

#### Detailed Comparison

| Dimension | P/Invoke via `c-shared` | Custom Anonymous Pipes | Local gRPC (HTTP/2 / UDS) |
| :--- | :--- | :--- | :--- |
| **Privilege Model** | Fatal Flaw: UI process must run with elevated network privileges (`root` or admin). | Passable: Daemon can run as root service; pipe permissions managed by OS. | **Optimal:** Daemon holds `CAP_NET_RAW`/admin; Avalonia UI runs as standard non-elevated desktop user. |
| **Fault Isolation** | None: A Go runtime panic or segmentation fault aborts the entire .NET process. | High: Daemon crash triggers EOF on pipe; UI remains active. | **High:** Daemon crash yields gRPC `Unavailable` status; UI attempts auto-reconnection. |
| **Cross-Compilation** | Nightmare: Requires target C toolchains (`x86_64-w64-mingw32`, multi-arch `gcc`) for every target. | Clean: Pure Go (`CGO_ENABLED=0`) and pure .NET SDK compilation. | **Clean:** Standard Go multi-platform build (`GOOS=windows/linux/darwin`) without CGO dependencies. |
| **Runtime Interference**| Go and .NET CLR compete for signal handlers (`SIGSEGV`, `SIGPIPE`, thread allocation). | Zero: Processes maintain independent virtual memory spaces and schedulers. | **Zero:** Independent execution models; Go M:N scheduler does not touch CLR thread pool. |
| **Wayland/GUI Safety** | Broken: Running GUI applications as root on modern Linux (Wayland) is blocked by design. | Safe: GUI operates in standard user session; connects via IPC file descriptor. | **Safe:** GUI communicates seamlessly over Loopback TCP (`127.0.0.1`) or Unix Domain Socket (`.sock`). |
| **Telemetry Streaming** | Complex: Requires pinned unmanaged memory, native-to-managed delegates, and GC handles. | High Boilerplate: Requires custom length-prefixed framing and JSON/binary serializers. | **Native:** Built-in HTTP/2 streaming mapping directly to C# `IAsyncEnumerable<T>`. |
| **Transfer Latency** | Sub-microsecond (Direct call) | 5–15 microseconds | **15–35 microseconds** (Negligible for 10–1000 Hz telemetry intervals). |

**Architectural Decision:** Adopt an **Out-of-Process Local gRPC Architecture**. P/Invoke creates severe security vulnerabilities, cross-compilation overhead, and desktop environment conflicts under Linux.

---

## 4. End-to-End Implementation: Go Core to Avalonia UI

```
 +-------------------------+                +-------------------------+
 |   Avalonia UI (.NET)    |                |     Go Daemon Core      |
 |  (Unprivileged Process) |                |   (Elevated Privileges) |
 +-------------------------+                +-------------------------+
 |                         |                |                         |
 |  TwampMonitorService    |                |  TelemetryEngineServer  |
 |            |            |                |            |            |
 |     GrpcChannel         |  gRPC Stream   |      gRPC Server        |
 |     (HTTP/2 / UDS)      | -------------> |     (Unix / Loopback)   |
 |            |            |  Proto Buffer  |            |            |
 |  IAsyncEnumerable<T>    | <------------- |   TWAMP Test Session    |
 |            |            |                |   (tcaine/twamp engine) |
 |  Reactive UI ViewModel  |                |            |            |
 |            |            |                |   Raw Sockets / ICMP    |
 |      HostCardView       |                |   (CAP_NET_RAW / Admin) |
 +-------------------------+                +-------------------------+
```

### 4.1 Protocol Buffers Specification (`madtom.proto`)

```protobuf
syntax = "proto3";

package madtom.telemetry;

option go_package = "madtom/pkg/telemetry/pb";
option csharp_namespace = "MadTom.Core.Grpc";

service TelemetryEngine {
  // Initiates an active TWAMP measurement stream
  rpc StreamTwampSession(TwampRequest) returns (stream TwampSample);

  // General node telemetry heartbeat stream
  rpc StreamNodeMetrics(NodeMetricsRequest) returns (stream NodeMetricSample);

  // Administrative command execution
  rpc ExecuteControl(ControlCommand) returns (ControlResponse);
}

message TwampRequest {
  string session_id = 1;
  string target_ip = 2;
  int32 port = 3;
  int32 packet_rate_hz = 4;
  int32 packet_size_bytes = 5;
  int32 dscp_value = 6;
}

message TwampSample {
  string session_id = 1;
  uint64 sequence_number = 2;
  double round_trip_time_ms = 3;
  double forward_jitter_ms = 4;
  double reverse_jitter_ms = 5;
  bool packet_lost = 6;
  int64 sender_timestamp_ns = 7;
  int64 receiver_timestamp_ns = 8;
}

message NodeMetricsRequest {
  int32 sampling_interval_ms = 1;
}

message NodeMetricSample {
  string host_uuid = 1;
  float cpu_utilization_pct = 2;
  uint64 memory_used_bytes = 3;
  uint64 memory_total_bytes = 4;
  uint64 tx_bytes_sec = 5;
  uint64 rx_bytes_sec = 6;
  int64 timestamp_ns = 7;
}

message ControlCommand {
  string command = 1;
  map<string, string> parameters = 2;
}

message ControlResponse {
  bool success = 1;
  string message = 2;
}
```

---

### 4.2 Go Telemetry Daemon (`madtomd`)

The Go daemon executes TWAMP measurement sessions using raw network capabilities and streams samples across an authenticated or loopback gRPC transport.

`cmd/madtomd/main.go`
```go
package main

import (
	"context"
	"fmt"
	"log"
	"math/rand"
	"net"
	"os"
	"os/signal"
	"syscall"
	"time"

	"google.golang.org/grpc"
	pb "madtom/pkg/telemetry/pb"
)

type TelemetryServer struct {
	pb.UnimplementedTelemetryEngineServer
}

func (s *TelemetryServer) StreamTwampSession(req *pb.TwampRequest, stream pb.TelemetryEngine_StreamTwampSessionServer) error {
	log.Printf("[TWAMP] Initializing session to target %s:%d @ %d Hz", req.TargetIp, req.Port, req.PacketRateHz)
	
	if req.PacketRateHz <= 0 {
		req.PacketRateHz = 1
	}
	
	interval := time.Duration(1000/req.PacketRateHz) * time.Millisecond
	ticker := time.NewTicker(interval)
	defer ticker.Stop()

	var sequence uint64 = 0

	for {
		select {
		case <-stream.Context().Done():
			log.Printf("[TWAMP] Session %s terminated by client", req.SessionId)
			return stream.Context().Err()
		case <-ticker.C:
			sequence++
			
			// Replace mock values with actual RFC 5357 TWAMP test frame transmission
			rtt := 12.0 + (rand.Float64() * 3.5)
			jitter := rand.Float64() * 0.8
			isLost := rand.Float64() < 0.01 // 1% synthetic loss simulation

			sample := &pb.TwampSample{
				SessionId:           req.SessionId,
				SequenceNumber:      sequence,
				RoundTripTimeMs:     rtt,
				ForwardJitterMs:     jitter,
				ReverseJitterMs:     jitter * 0.9,
				PacketLost:          isLost,
				SenderTimestampNs:   time.Now().UnixNano(),
				ReceiverTimestampNs: time.Now().UnixNano() + int64(rtt*1e6),
			}

			if err := stream.Send(sample); err != nil {
				log.Printf("[TWAMP] Failed to push sample: %v", err)
				return err
			}
		}
	}
}

func main() {
	socketPath := "127.0.0.1:50051"
	listener, err := net.Listen("tcp", socketPath)
	if err != nil {
		log.Fatalf("Failed to bind socket: %v", err)
	}

	grpcServer := grpc.NewServer()
	serverInstance := &TelemetryServer{}
	pb.RegisterTelemetryEngineServer(grpcServer, serverInstance)

	// Graceful shutdown handling
	sigChan := make(chan os.Signal, 1)
	signal.Notify(sigChan, os.Interrupt, syscall.SIGTERM)
	go func() {
		<-sigChan
		log.Println("[SHUTDOWN] Intercepted termination signal. Stopping gRPC...")
		grpcServer.GracefulStop()
		os.Exit(0)
	}()

	log.Printf("[STARTUP] Mad TOM Telemetry Core listening on %s", socketPath)
	if err := grpcServer.Serve(listener); err != nil {
		log.Fatalf("Fatal gRPC server error: %v", err)
	}
}
```

---

### 4.3 Avalonia Client Engine (C#)

The client consumes the gRPC stream using modern C# asynchronous streams (`IAsyncEnumerable<T>`), delegating data handling directly to Avalonia’s UI thread or reactive stores without blocking the main event loop.

`Services/TwampMonitoringService.cs`
```csharp
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using MadTom.Core.Grpc;

namespace MadTom.Client.Services;

public sealed class TwampMonitoringService : IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly TelemetryEngine.TelemetryEngineClient _client;

    public TwampMonitoringService(string endpoint = "http://127.0.0.1:50051")
    {
        // Unencrypted HTTP/2 loopback configuration
        _channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions
        {
            HttpHandler = new System.Net.Http.SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                KeepAlivePingDelay = TimeSpan.FromSeconds(15),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(5)
            }
        });

        _client = new TelemetryEngine.TelemetryEngineClient(_channel);
    }

    public async IAsyncEnumerable<TwampSample> MonitorTargetAsync(
        string targetIp, 
        int port, 
        int rateHz, 
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = new TwampRequest
        {
            SessionId = Guid.NewGuid().ToString("N"),
            TargetIp = targetIp,
            Port = port,
            PacketRateHz = rateHz,
            PacketSizeBytes = 64,
            DscpValue = 46 // Expedited Forwarding (EF)
        };

        using var streamCall = _client.StreamTwampSession(request, cancellationToken: ct);

        while (await streamCall.ResponseStream.MoveNext(ct))
        {
            yield return streamCall.ResponseStream.Current;
        }
    }

    public void Dispose()
    {
        _channel.Dispose();
    }
}
```

`ViewModels/HostMonitorViewModel.cs`
```csharp
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadTom.Client.Services;
using MadTom.Core.Grpc;

namespace MadTom.Client.ViewModels;

public partial class HostMonitorViewModel : ObservableObject
{
    private readonly TwampMonitoringService _telemetryService;
    private CancellationTokenSource? _monitorCts;

    [ObservableProperty]
    private double _currentRttMs;

    [ObservableProperty]
    private double _currentJitterMs;

    [ObservableProperty]
    private bool _isPacketLossDetected;

    [ObservableProperty]
    private bool _isMonitoring;

    public HostMonitorViewModel(TwampMonitoringService telemetryService)
    {
        _telemetryService = telemetryService;
    }

    [RelayCommand]
    public async Task ToggleMonitoringAsync()
    {
        if (IsMonitoring)
        {
            _monitorCts?.Cancel();
            IsMonitoring = false;
            return;
        }

        IsMonitoring = true;
        _monitorCts = new CancellationTokenSource();

        try
        {
            var stream = _telemetryService.MonitorTargetAsync(
                "192.168.1.1", 
                862, 
                10, 
                _monitorCts.Token);

            await foreach (var sample in stream)
            {
                CurrentRttMs = Math.Round(sample.RoundTripTimeMs, 2);
                CurrentJitterMs = Math.Round(sample.ForwardJitterMs, 2);
                IsPacketLossDetected = sample.PacketLost;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            // Handle disconnection / daemon restart
            Console.Error.WriteLine($"Telemetry stream faulted: {ex.Message}");
        }
        finally
        {
            IsMonitoring = false;
        }
    }
}
```

---

## 5. Deployment, Process Supervision & Security Boundaries

### 5.1 Daemon Orchestration Models

#### Mode A: Integrated Desktop Mode (Zero-Config Developer Experience)
When Avalonia launches on an unconfigured system:
1. Avalonia issues a pre-flight gRPC ping to `127.0.0.1:50051`.
2. If unreachable, `System.Diagnostics.Process` spawns the packaged `madtomd` executable as a managed child process.
3. The parent process hooks the child's `StandardError` and binds its termination lifecycle to standard GUI exit signals.

#### Mode B: Enterprise System Service Mode (Production Deployment)
1. The Go daemon runs as a continuous system service (`systemd` unit on Linux, Windows Service on NT).
2. The UI client runs completely unprivileged under the desktop user session.
3. The UI connects across either:
   * **Unix Domain Socket:** `/run/madtom/telemetry.sock` (File permission-based access control).
   * **Secured Loopback TCP:** `127.0.0.1:50051`.

### 5.2 Linux Capability Hardening (Privilege Separation)
To bypass requiring `root` for TWAMP, ICMP, and raw socket construction, configure Linux capabilities on the compiled Go binary:

```bash
# Compile pure static Go binary
CGO_ENABLED=0 GOOS=linux go build -ldflags="-s -w" -o madtomd ./cmd/madtomd

# Grant packet capture and network configuration rights without root execution
sudo setcap cap_net_raw,cap_net_admin+ep ./madtomd

# Verify assigned capabilities
getcap ./madtomd
# Output: ./madtomd = cap_net_admin,cap_net_raw+ep
```

This prevents the GUI desktop process from ever needing elevated privileges, fully avoiding Wayland display socket execution restrictions.

---

## 6. Architecture Verification Summary

```
+------------------------------------------------------------------------------------+
|                                MAD TOM ECOSYSTEM                                   |
+------------------------------------------------------------------------------------+
|  UI Presentation Layer (Avalonia UI / .NET)                                        |
|   - Dynamic Lexicon Engine (JSON-driven, real-time in-place UI string updates)     |
|   - Personas: Goose (Honk/Gander), Standard (Heartbeat/Host), Feline (Meow/Tomcat) |
|   - Asynchronous gRPC Client with IAsyncEnumerable consumption                     |
+------------------------------------------+-----------------------------------------+
                                           |
                                   gRPC Protocol (HTTP/2)
                           (Unix Domain Socket or 127.0.0.1:50051)
                                           |
+------------------------------------------+-----------------------------------------+
|  Telemetry Engine Core (Go / madtomd)                                              |
|   - Deep network protocol implementations (TWAMP RFC 5357, Precision Jitter)       |
|   - Runs unencumbered by CLR runtime or GC interop signals                         |
|   - Confined privileges via Linux Capabilities (CAP_NET_RAW)                       |
|   - Pure Go compilation across cross-platform architectures                        |
+------------------------------------------------------------------------------------+
```
