Viewed network-topology-visualizer-plan.md:1-249

### Deep Architectural Analysis & System Blueprint: Network & Topology Visualizer

Having thoroughly reviewed [`planning/19-12/network-topology-visualizer-plan.md`](file:///home/danial/Programming/Projects/CSharp/MADTOM/planning/19-12/network-topology-visualizer-plan.md), I have set aside high-level code sketches to focus on the deep architectural details, kernel boundaries, state machines, and UX lifecycle requirements.

---

### 1. Control Plane & Reverse Dispatch (The Push/Pull Problem)

In MADTOM, monitored nodes connect either via **PUSH mode** (node dials collector outbound and maintains a reverse streaming channel) or **PULL mode** (collector dials node directly).

Because inspection must be **strictly on-demand** with **zero idle overhead**, volatile inspection cannot poll continuously in the background. The core challenge is how an interactive user session in MADTOM Studio reaches a node that is operating in PUSH mode behind a firewall or NAT:

```
[ Studio Desktop ]  --- gRPC SubscribeInspection(node_id, lease) --->  [ madtom-collector ]
                                                                             │
                    ┌────────────────────────────────────────────────────────┴───────────────────────────────────────────────────────┐
                    ▼ (Mode A: Node in PUSH mode)                                                                                   ▼ (Mode B: Node in PULL mode)
         [ Ingest Reverse Channel ]                                                                                      [ Direct Node Dial ]
         Collector sends `InspectionSessionDirective` on `BatchAck` stream.                                             Collector dials `madtomd:50052`
         Node daemon opens an ephemeral duplex gRPC inspection channel                                                  `NetworkInspectionService` directly.
         back to collector, bound to the active session lease.
```

#### Lifecycle & Renewable Lease Guarantee
- **UI Visibility Bound**: The inspection session is instantiated **only** when `HostDetailViewModel.ActiveTab == "flight"` **and** the host window is visible/active.
- **Heartbeat & Leases**: Studio transmits a heartbeat renewing an ephemeral lease every **5 seconds**. If no renewal arrives within **15 seconds** (e.g. Studio tab switch, minimization, or crash), the collector and node daemon automatically abort all kernel dumps, kill child diagnostic processes, and free memory buffers.
- **Immediate Cancellation**: When the user navigates away from the tab or changes the active host node, Studio immediately sends a cancellation RPC; in-flight probers on the node are canceled via Go `context.WithCancel()` within **<500ms**.

---

### 2. Linux Kernel Ingestion & Data Fidelity

To ensure high performance without shell script overhead or high CPU spikes, the daemon uses direct Linux kernel interfaces rather than executing human-readable CLI wrappers:

```
┌────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                       Linux Kernel Interfaces                                         │
├──────────────────────────┬─────────────────────────────┬────────────────────────┬──────────────────────┤
│       rtnetlink          │         sock_diag           │        nftables        │     Diagnostics      │
│  (NETLINK_ROUTE sockets) │   (NETLINK_INET_DIAG)       │      (JSON API)        │      (Sandbox)       │
├──────────────────────────┼─────────────────────────────┼────────────────────────┼──────────────────────┤
│ • RTM_GETLINK:           │ • INET_DIAG_REQ_V2:         │ • nft -j -a list       │ • /bin/ping -c <N>   │
│   ifindex, flags, MTU,   │   Dumps TCP socket table    │   ruleset:             │   (ICMP echo,        │
│   carrier, MAC, speeds.  │   without text /proc/net/tcp│   Structured JSON dump │   bounded ≤10 pkts,  │
│ • RTM_GETADDR:           │   allocation bottlenecks.   │   preserving tables,   │   2s socket timeout) │
│   All IPv4/IPv6 CIDRs.   │ • INET_DIAG_INFO:           │   chains, hooks, hook  │ • traceroute -n      │
│ • RTM_GETROUTE:          │   Direct tcp_info parsing:  │   priorities, handles, │   (Max 30 hops,      │
│   FIB tables (main &     │   tcpi_delivery_rate (bps), │   rules, and per-rule  │   3 probes/hop,      │
│   custom), metrics, hops.│   tcpi_pacing_rate (bps),   │   packet/byte counters.│   30s hard timeout)  │
│ • RTM_GETNEIGH:          │   tcpi_min_rtt, tcpi_rtt,   │ • Detects legacy       │ • Strict FQDN/IP     │
│   ARP & NDP cache.       │   tcpi_snd_cwnd, ssthresh.  │   iptables as an       │   regex validation   │
│ • RTM_GETRULE:           │ • tcpi_ca_state & algorithm │   explicit capability  │   (no shell parsing, │
│   FIB policy routing.    │   name ("bbr", "cubic").    │   boundary.            │   no injection).     │
└──────────────────────────┴─────────────────────────────┴────────────────────────┴──────────────────────┘
```

#### Avoiding Common Misrepresentations:
1. **Never Invent Topology**: A default route `0.0.0.0/0 via 192.168.1.1` confirms a next-hop gateway, but does **not** prove upstream ISP physical router nodes. The canvas must distinguish between *Configured Gateway*, *Observed Neighbor (ARP)*, and *Traceroute Hop*.
2. **Multi-Identity Keying**: Network interfaces are keyed by `(NodeId, BootId, Namespace, IfIndex)`. Network interface names can be renamed dynamically (e.g. `udev` renames or interface deletion/recreation); ifindex + boot generation prevents stale counter attribution.
3. **Socket Identity Stability**: TCP sockets can reuse endpoints `(IP:Port)`. Sockets are keyed by kernel socket cookie / inode + start timestamp to ensure throughput deltas are not misattributed across recycled connections.
4. **Explicit Capability Boundaries**: If a node runs an older kernel without `inet_diag` BBR delivery rates, or runs with unprivileged service permissions lacking `CAP_NET_ADMIN` for nftables, the UI displays `Unavailable (CAP_NET_ADMIN required)` rather than fabricating zeroes.

---

### 3. Progressive Loading Pipeline & Inspector State Machine

The progressive loading architecture prevents the desktop app from freezing or waiting for a monolithic multi-megabyte payload.

#### Staged Delivery Timeline
| Stage | Node Probe Operation Subtitle | Immediate Desktop Rendering Action |
| :--- | :--- | :--- |
| **0: Capabilities** | `node-a · Checking network inspection capabilities in root namespace` | Initializes session badge, enables available views, flags disabled kernel features. |
| **1: Devices** | `node-a · Interfaces · RTM_GETLINK dump` | **Canvas immediately draws Interface Cards** (eth0, wlan0, wg0). Inspector Link Info is Ready; route/socket tabs show loading shimmers. |
| **2: Addresses & Neighbors** | `node-a · Addresses · RTM_GETADDR dump · Neighbors · RTM_GETNEIGH dump` | Merges IPv4/IPv6 address badges into interface cards; draws ARP/NDP neighbor nodes. |
| **3: Routing** | `node-a · IPv4/IPv6 routes · RTM_GETROUTE dump · Policy rules · RTM_GETRULE dump` | **Canvas connects routing edges** to default gateways and local subnets; populates Routing Table view. |
| **4: Firewall** | `node-a · Firewall · running nft -j -a list ruleset` | Attaches shield badges (`[🛡️ 24 rules]`) to affected interfaces; populates chain/rule inspector. |
| **5: Connections** | `node-a · TCP sockets · inet_diag dump, page 1 (500 sockets)` | Populates Active Connections table, TCP congestion chips (`BBR`, `CUBIC`), and endpoint links. |
| **6: Live Rates** | `node-a · eth0 counters · collecting second delta sample` | Dual sparklines begin plotting live rates; status subtitle transitions to *"Topology synchronized"*. |

#### Per-Section Independent Inspector State Machine
Each sub-panel in the inspector drawer operates on an independent state machine:

```
   ┌─────────────────────────────────────────────────────────┐
   │                       Uninitialized                     │
   └────────────────────────────┬────────────────────────────┘
                                │ (Session Starts)
                                ▼
   ┌─────────────────────────────────────────────────────────┐
   │            Loading (Exact Node Operation Subtitle)      │
   └───────┬────────────────────┬────────────────────┬───────┘
           │ (Payload Arrives)  │ (Perm/Missing)     │ (Kernel Error)
           ▼                    ▼                    ▼
   ┌───────────────┐    ┌───────────────┐    ┌───────────────┐
   │     Ready     │    │  Unavailable  │    │     Error     │
   └───────┬───────┘    └───────────────┘    └───────────────┘
           │ (Tab Refresh / Incremental Delta)
           ▼
   ┌───────────────┐
   │ Stale (Cached)│
   └───────────────┘
```

If the user clicks an interface during Stage 1:
- **Interface & Link Section**: `Ready` (shows MTU, MAC, Carrier status).
- **Addresses & Neighbors Section**: `Loading` (*"Querying RTM_GETADDR on node..."*).
- **Routing Section**: `Loading` (*"Waiting for Stage 3 (Routing)..."*).
- **Firewall Section**: `Loading` (*"Waiting for Stage 4 (nftables)..."*).
- **Active Sockets Section**: `Loading` (*"Waiting for Stage 5 (inet_diag)..."*).

No stage blocks any other stage, and a slow nftables dump (e.g. 10,000 rules) never delays the rendering of interfaces or routes.

---

### 4. Interactive Canvas (Map) & UX Stability Rules

To ensure dense yet clean rendering, the canvas separates visual layout from kernel state:

1. **Non-Destructive Movement**:
   - Interfaces, Gateways, and Subnets can be dragged freely.
   - Drag coordinates are saved to client storage (`~/.local/share/MADTOM/topology-layout-{nodeId}.json`).
   - Movement changes **only visual presentation coordinates**, never routing or kernel settings.
2. **Stable Layout Guarantees**:
   - Incremental arrival of Stages 2, 3, or 4 **must never restart layout, move user-pinned cards, close the inspector, or deselect the active object**.
   - Auto-layout applies only to newly discovered objects.
   - An explicit `"Reset Layout"` button restores the default hierarchical layout:
     $$\text{Host Physical/Virtual NICs} \longrightarrow \text{Default Gateways / Local Subnets} \longrightarrow \text{WAN / Remote Endpoints}$$
3. **Semantic Zooming & Decluttering**:
   - **Far Zoom**: Interfaces display as compact colored pill chips with link status and aggregate throughput.
   - **Medium Zoom**: Cards display primary IP, carrier state, and RX/TX sparklines.
   - **Close Zoom**: Displays MAC, MTU, packet drop badges, and rule counts.
   - **Socket Clustering**: Remote TCP connections are clustered by destination IP/subnet to avoid creating a massive "spaghetti" graph with thousands of overlapping lines.
4. **Directional Flow & Traffic Weighting**:
   - Curved Bezier edges link interfaces to next-hops.
   - Edges use stroke-dash animations weighted by real-time throughput ($>1\text{ MB/s}$ glows brighter with faster dash flow; idle routes remain static thin lines).

---

### 5. Read-Only Diagnostics & TCP Congestion Introspection

#### Active Diagnostic Probes
- **Explicit Trigger Only**: Probes (`ping`, `traceroute`, `route get`) **never execute automatically** upon opening or clicking a node. The user must explicitly click the action button in the inspector.
- **Strict Bounding**:
  - `Ping`: Exactly 4 packets, 2-second timeout per packet.
  - `Traceroute`: Maximum 30 hops, 3 probes per hop, 15-second total execution deadline.
- **Server-Side Validation**: Target inputs are validated on the node daemon using strict IP and hostname regexes; remote shell strings are prohibited.
- **Real-Time Streaming**: As ping ICMP replies or traceroute hop lines arrive, they are streamed via gRPC directly to a mini-terminal emulator in the inspector drawer.

#### TCP Congestion Control Metrics Fidelity
- Shows system default algorithm (`sysctl net.ipv4.tcp_congestion_control`) and all kernel-supported algorithms (`tcp_available_congestion_control`).
- Dissects per-socket `tcp_info`:
  - Algorithm tag (`BBR`, `CUBIC`, `RENO`).
  - Delivery throughput vs. pacing rate.
  - Min RTT vs. smoothed RTT.
  - Congestion window (`cwnd`) and slow start threshold (`ssthresh`).
  - Retransmission counters.

---

### 6. Phased Implementation Sequence

| Phase | Core Deliverable | Status | Completion & Verification Gate |
| :--- | :--- | :--- | :--- |
| **Phase 1: Contracts & Transport** | `network_topology.proto`, Go/C# generation, session lease & cancellation protocol. | **COMPLETED** | Protobuf generated in Go & C#; builds clean across both stacks. |
| **Phase 2: Linux Probers (Go)** | `rtnetlink` links/routes, `inet_diag` sockets, `nftables` JSON parser, sandboxed diagnostic runner. | **COMPLETED** | Tested on Linux: netlink interfaces, addresses, routes, sockets, nftables, ping/traceroute. Go tests 100% PASS. |
| **Phase 3: Desktop Staged ViewModel** | `NetworkTopologyViewModel`, progressive streaming state machine, per-section inspector states. | **COMPLETED** | Progressive staging with exact operation subtitles, heartbeats, and non-blocking background streaming implemented. |
| **Phase 4: Avalonia Map Canvas** | Custom `TopologyCanvasControl`, pan/zoom, draggable node cards, Bezier edge rendering, layout persistence. | **COMPLETED** | 2D canvas with pan/zoom, draggable cards, Bezier next-hop curves, and JSON coordinates persistence. |
| **Phase 5: Inspector & Diagnostics** | Detailed routes table, nftables rules, TCP socket table with BBR/metrics, live ping/traceroute terminal. | **COMPLETED** | Dense inspector drawer with tabs (Routes, Firewall Rules, Sockets, ARP/Neighbors, Diagnostics). |
| **Phase 6: Integration & Verification** | End-to-end deployment to dev machine (`lbw`), live testing on physical devices (`tp1`, `tp2`). | **COMPLETED** | Deployed Go daemons to `tp1` & `tp2`, collector to `lbw`, Studio compiled, all 244 .NET tests PASS. |
| **Phase 7: UX Polish & Canvas Layout** | Tab collision fix, auto arrange algorithm, center graph, card text wrapping & tooltips. | **COMPLETED** | Fixed Avalonia `Converter={x:Null}` tab overlap bug, added 4-tier auto arrange, centering, card text wrapping, and hover tooltips. All tests PASS. |
| **Phase 8: Node Lifecycle & Canvas Coordinate Fidelity** | Complete wipe on node switch, exact card hit detection, removal of artificial boundary walls. | **COMPLETED** | `ClearTopologyData()` called on deactivation/switch; removed artificial clamp walls (`Math.Max(0, ...)`); synchronized card measurement with pointer hit/drag tracking (`node.Height`). All 244 tests PASS. |
| **Phase 9: Real-time Invalidation & Node Classification** | Immediate canvas repaint on stream traffic arrival, icon glyphs, and colored type badges. | **COMPLETED** | Connected `CanvasInvalidationRequested` via UI thread dispatcher whenever `ApplyTrafficRates` receives deltas; added icon glyphs (host/bridge/loopback/veth/wifi/wireguard/gateway) and colored badges on cards. All 244 tests PASS. |
| **Phase 10: Matrix Transform Order & Exact Mouse Inversion** | Fixed matrix multiplication associativity discrepancy in Avalonia rendering context. | **COMPLETED** | Aligned render matrix to `CreateScale * CreateTranslation`; implemented exact `transform.TryInvert(out var inv)` in `ScreenToWorld`. Pointer hit testing is now pixel-perfect across all zoom and pan offsets. All 244 tests PASS. |
| **Phase 11: Decoupled Resizable Inspector & Real Firewall Detection** | Independent collapsible/resizable drawer, GridSplitter, true backend badge (nftables vs. iptables-legacy). | **COMPLETED** | Added `GridSplitter` and `InspectorWidth` binding (min 320px, max 900px); inspector remains open by default showing global tables without requiring node clicks; added top-bar toggle button; replaced cosmetic label with true detected kernel backend (`NFTABLES`, `IPTABLES (LEGACY)`, or `NONE`). All 244 tests PASS. |
| **Phase 12: Tunnel Loopback Guard & Direct Local Inspection** | Loopback proxy guard in push server, direct workstation daemon inspection, exact kernel CC and firewall versioning. | **COMPLETED** | Fixed SSH tunnel loopback hijacking in `push_server.go` where `127.0.0.1` masked remote push clients with collector host stats; added direct local daemon inspection at `127.0.0.1:50052` in Studio for workstation nodes (`yoga`); added daemon-side `NodeId` validation in `NetworkInspectionServer`. Verified true kernel stats (`yoga` -> `bbr`, `nftables v1.1.7`). All 247 .NET tests and 100% Go tests PASS. |
| **Phase 13: Push Address Persistence & Session Thrashing Hardening** | Resolved intermittent "node address not registered" error on long runtime push clients, dead gRPC conn eviction, and Studio loading race. | **COMPLETED** | 1) Fixed Studio `ActivateForNode` cancellation loop when telemetry batches arrived while `IsLoading == true`. 2) Added `node_addresses.json` disk persistence in `Registry` so push daemon addresses survive collector restarts. 3) Added dead conn eviction (`removeConn`) on stream/RPC errors in `NetworkInspectionRelayServer`. 4) Updated collector hostname fuzzy matching (`isLocalCollectorNode`) for `dani-lbw` <-> `lbw` and safe IPv6 formatting (`net.JoinHostPort`). All 247 .NET tests and 100% Go tests PASS. |

---

### Implementation Status: Complete
All 13 phases of the Network & Topology Visualizer blueprint have been implemented, tested, verified, and documented across MADTOM-Collector (`lbw`), MADTOM-Daemon (`tp1`, `tp2`, `yoga`), and MADTOM Studio.