# MADTOM telemetry

Build from this directory with `go build -o bin/madtom-daemon ./cmd/madtom-daemon`
and `go build -o bin/madtom-collector ./cmd/madtom-collector`.
Rebuild the C# app too: the protobuf contract now carries process snapshots,
hardware metadata, configuration delivery, and TWAMP measurements.

## Connection patterns

Node IDs must match the target IDs configured on the collector. Use distinct
spool directories for separate daemon instances. The daemon defaults `-node-id` to
its operating-system hostname; the name before `@` is an exact node ID, not an alias.

If polls report `node ID mismatch`, check the daemon startup log's `Node:` value
and use that exact value in `-pull-targets ID@ADDRESS:50052` (or
`-reverse-push-targets`). Alternatively, explicitly set the daemon's `-node-id`
to the desired ID. Prefer correcting the collector target for an existing daemon
to preserve its telemetry identity. Nodes appear in the UI after successful
ingestion; merely configuring a target does not register a discovered node.

| Pattern | Daemon | Collector |
| --- | --- | --- |
| Daemon listens; collector polls | `-mode pull -node-id node-1 -listen-port 50052` | `-pull-targets node-1@NODE_ADDRESS:50052` |
| Collector listens; daemon pushes | `-mode push -node-id node-1 -collector COLLECTOR_ADDRESS:50051` | `-port 50051` |
| Daemon listens; collector connects; daemon pushes | `-mode reverse-push -node-id node-1 -listen-port 50052` | `-reverse-push-targets node-1@NODE_ADDRESS:50052` |

Collectors can receive inbound push streams and connect to multiple pull and
reverse-push targets concurrently (comma-separated lists). The collector's query
port is still needed for the UI even when all daemon connections are outbound.
Do not configure multiple consumers for the same daemon spool.

Daemons sample independently of connectivity. Batches remain on disk until a
matching acknowledgement arrives, subject to the configured bounded spool quota.
The collector syncs writes before acknowledging. Replays use identical timestamp
keys, so interrupted transfers do not create duplicate historical points.
Collection settings saved in the UI are delivered in the next poll or batch ACK.

## TWAMP

Configure an existing **TWAMP Light, unauthenticated UDP reflector** using
`-twamp-target REFLECTOR_ADDRESS:PORT` on the daemon, or the node settings in the UI.
An empty target disables probing. Timeouts and missing configuration are reported
as unavailable, and are not stored as zero-delay samples.

RTT is measured using the reflector's receive/transmit timestamps to exclude its
processing time. One-way measurements require the operator's
`-twamp-clocks-synchronized` assertion (also available in the UI) and the reflector's
synchronization bit. No one-way latency is inferred by dividing RTT by two.
This implements TWAMP Light test packets, not TWAMP-Control negotiation or the
authenticated/encrypted variants. See [RFC 5357 section 4 and Appendix I](https://www.rfc-editor.org/rfc/rfc5357).

## UI

Select a node to see its actual logical-thread count, memory, process snapshots,
and NIC counters. Network topology describes observed node/collector connections;
geographic paths, ASN/protocol attribution, remote logs, and process signals are
not available. Signal controls are disabled for collector-backed nodes.

The metrics tab queries stored CPU, memory, power, NIC, and TWAMP series. Select a
metric and **Add graph**; **Remove** deletes a graph from the layout. Layout is saved
under the user's local application data `MADTOM/graphs.json`. Preset and custom
scopes change query bounds. Relative scopes refresh as telemetry arrives; custom
windows remain fixed. NIC history is cumulative bytes since boot, while fleet
sparklines display rates calculated from successive counters. Empty history stays
empty instead of displaying generated samples.

Configuration and node discovery in the collector are currently in-memory;
historical scalar metrics are persisted in Pebble. Restarted nodes repopulate
live metadata and process snapshots. Collector addresses and per-node viewing
preferences are currently session settings.

## Deployment

Deploy the Go daemon or collector to a remote system via SSH with the included `deploy.sh`:

```bash
# Interactive run (prompts for SSH user/host and password):
./deploy.sh

# Or pass host directly:
./deploy.sh user@192.168.1.50

# Deploy collector instead of daemon with custom paths:
./deploy.sh -u admin -h 10.0.0.15 \
  -b bin/madtom-collector \
  -d /opt/madtom-collector \
  -s madtom-collector.service
```

The script uploads the binary to `/tmp`, uses `sudo` with the provided password to copy it to the destination path (default: `/opt/madtomd`), and restarts the systemd service. Default configuration parameters can also be modified at the top of `deploy.sh`.

Example systemd unit files documenting every command-line flag are provided in:
- [`systemd/madtom-daemon.service`](file:///home/danial/Programming/Projects/CSharp/MADTOM/MADTOM_GOLANG/systemd/madtom-daemon.service)
- [`systemd/madtom-collector.service`](file:///home/danial/Programming/Projects/CSharp/MADTOM/MADTOM_GOLANG/systemd/madtom-collector.service)

## Verification

`go test -race ./...` covers ingestion, listening-daemon modes, WAL recovery,
TWAMP packets, downsampling, and storage. From the repository root run
`dotnet test MADTOM_DOTNET/MadTOM.Tests/MadTOM.Tests.csproj` for UI model and history
regressions. Integration tests require permission to bind local sockets.
