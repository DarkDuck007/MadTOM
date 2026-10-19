# Network & Topology visualizer plan

Status: proposed implementation plan; no runtime changes included.
Prepared: 2026-10-12. Target: MADTOM Telemetry, Linux node inspection.

## 1. Intended experience

Turn Network & Topology into an interactive network map backed by complete, searchable interface, routing, firewall, and connection details. The map answers “what connects to what, through which interface, and what is happening now?” Selecting an object answers the detailed questions without filling the canvas with tables or every firewall rule.

Requirements:

- Interfaces, hosts, gateways, and observed intermediate routers can be dragged, selected, pinned, and inspected. Panning and zooming behave like a map.
- All collected interface information remains accessible through the inspector, tables, and raw fields; compact presentation must not discard data.
- Routing policy and nftables rules are first-class views linked to the map.
- Traffic, TCP connection state, per-socket congestion control, and available throughput measurements are inspectable.
- Diagnostics are explicitly invoked, bounded ping/traceroute and route lookup operations. No network configuration mutation is exposed.
- Additional volatile inspection runs only while the relevant Network & Topology surface is visible. Initial stages render independently as their results arrive.
- A progress bar includes an exact node-side operation subtitle. Each inspector section has its own loading, ready, unavailable, partial, stale, or error state.

“Read-only” means no configuration changes. Ping and traceroute send packets and can affect counters and ephemeral kernel state; label them as active probes. Merely opening or clicking the map must never launch them.

## 2. Current implementation and integration points

Repository inspection shows two related surfaces:

- [HostFlightTabViewModel](../../MADTOM_DOTNET/src/Plugins/Telemetry/MADTOM.Plugins.Telemetry/ViewModels/HostFlightTabViewModel.cs) and its host tab render interface counters and an unavailable-firewall-attribution message. The `flight` tab is labeled Network & Topology by the lexicon.
- [GlobalRadarViewModel](../../MADTOM_DOTNET/src/Plugins/Telemetry/MADTOM.Plugins.Telemetry/ViewModels/GlobalRadarViewModel.cs) renders fleet interface counters and node-to-collector relationships. Those relationships are telemetry transport relationships, not discovered physical network paths.
- Both subscribe to provider updates from their constructors. Construction or ordinary fleet updates must not trigger the new inspection jobs.
- [HostDetailViewModel](../../MADTOM_DOTNET/src/Plugins/Telemetry/MADTOM.Plugins.Telemetry/ViewModels/HostDetailViewModel.cs) owns host/tab selection; [MainViewModel](../../MADTOM_DOTNET/src/Plugins/Telemetry/MADTOM.Plugins.Telemetry/ViewModels/MainViewModel.cs) owns navigation. Visibility must combine both levels.
- [ITelemetryDataProvider](../../MADTOM_DOTNET/src/Plugins/Telemetry/MADTOM.Plugins.Telemetry/Services/ITelemetryDataProvider.cs), `CollectorTelemetryDataProvider`, and `CollectorClientService` are the desktop data boundary. Existing interface counters are insufficient for routes, complete nftables rules, or per-socket metrics.
- Go telemetry contracts live in [MADTOM_GOLANG/proto/madtom/v1](../../MADTOM_GOLANG/proto/madtom/v1). Collector RPC handling is under `pkg/telemetry/collector/api`; node collection is under `pkg/telemetry/daemon/collector`, including `net_linux.go`.

Use one shared topology component and inspection service for the host tab and radar. Deliver the host tab first. Fleet scope initially shows cached host summaries and allows explicit node expansion; it must not inspect every host automatically.

## 3. Layout and information density

```text
[Host / namespace / VRF] [Search] [IPv4 | IPv6 | Both] [Refresh] [Pause]
[Loading: 4 of 7 stages complete ━━━━━━━━━░░░░░░░░░░░░░░░░░░░░░░░]
[node-a · Firewall · running nft -j -a list ruleset · 1.2 s]
┌────────────────────────────────────────┬──────────────────────────┐
│ Map / Routes / Firewall / Connections  │ Selected object          │
│                                        │ Summary and status       │
│       host ─ eth0 ─ gateway ─ …         │ Addresses / relationships│
│         └─ br0 ─ veth / namespace      │ Counters and mini charts │
│                                        │ Details / raw fields     │
│ [Fit] [Zoom] [Layers] [Legend]          │ [Ping] [Traceroute]      │
├────────────────────────────────────────┴──────────────────────────┤
│ Context table or probe results (resizable / collapsible)           │
└───────────────────────────────────────────────────────────────────┘
```

- Default canvas cards show name, object type, link state, primary address, and RX/TX rate. Use compact badges for additional addresses, routes, rules, errors, and connections.
- Use semantic zoom: clusters at fleet scale, interface labels at host scale, extra counters at close scale. Keep full labels in selection/hover details.
- Use a persistent right inspector, approximately 320–440 logical pixels when space permits; use a drawer at narrow widths. Avoid multiple overlapping popovers.
- Route and firewall modes reuse the selection and inspector. Full tables can expand to fill the workspace; the map remains recoverable without losing position.
- Show selected paths prominently and unrelated edges dimmed. Keep interface, routed path, observed traceroute, and collector transport layers individually toggleable.
- Virtualize tables, support column selection, sorting, address/prefix search, and filters. Display total/matching/loaded counts, with explicit pagination or truncation notices.
- Provide keyboard navigation, non-drag layout controls, focus indicators, text/icon status alongside color, tooltips, and reduced-motion behavior.

### Map behavior and identity

- Dragging changes only local presentation coordinates. Persist position, pin state, and viewport by node/scope; never interpret movement as a routing change.
- Incremental data enrichment must not restart layout, move pinned objects, close the inspector, or lose selection. Auto-layout affects new/unpinned objects; explicit Reset layout restores defaults.
- Key interfaces by node identity, boot/session identity, namespace identity, and ifindex; names are mutable. Detect reuse after deletion. Include family and scope in neighbor/path identities; identical private addresses across hosts are not one router.
- Represent physical NICs, bridges, bonds, VLANs, tunnels, veth pairs, loopback, namespaces, and VRFs with distinct types and relationship labels where reported.
- Render configured gateways before neighbor resolution completes. Missing MAC or unanswered probes mean unknown, not offline.
- Keep configured, directly observed, probe-observed, and inferred edges visually distinct, with source and observation time available on click. Never invent intermediate routers from a default route.
- Traceroute adds ordered hop observations, including unanswered hops and multiple responders. It does not prove a stable physical path or symmetric return path. An unmanaged hop inspector contains only observed facts; node-side metrics require a monitored node association supported by evidence.

## 4. Data coverage and drill-down

Every section includes source, sampled time, freshness, capability state, and raw/extension fields. Preserve unrecognized fields and represent unsupported fields as unavailable, never fabricated zeroes. “Complete” means all fields obtained from the supported read adapters; permission and kernel limits remain visible.

| Object | Compact presentation | Full inspector/table coverage |
| --- | --- | --- |
| Interface | Name, kind, state, primary IP, RX/TX | ifindex, flags, admin/oper state, carrier, MTU, MAC, all IPv4/IPv6 addresses with scope/lifetimes, link speed/duplex when known, parent/master/peer relationships, namespace/VRF, queue information, RX/TX bytes and packets, separate drops/errors, driver counters where supported |
| Route | Prefix → next hop/interface, table, metric | IPv4/IPv6, all exposed tables, destination/source selectors, route type/scope/protocol, preferred source, metric, expiry, next-hop objects, multipath weights, on-link flags, encapsulation and unknown attributes |
| Routing policy | Priority, match, target | Rule order, table lookup, source/destination, interface selectors, mark/mask, UID/protocol/ports where exposed, suppressors and terminal actions |
| Neighbor | IP, interface, reachability | MAC, family, neighbor state/flags, timestamps where supplied; absence is not proof of a disconnected link |
| Firewall | Family/table/chain, hook, policy, rule count | Chain type/hook/priority/device, policy, ordered expressions, handles/comments, jump/goto/return, sets/maps and elements, named objects, NAT, counters and raw JSON |
| TCP socket | Endpoints, state, algorithm, rate | Namespace, socket identity, interface association confidence, process owner when permitted, queues, RTT/variance, retransmits, cwnd, ssthresh, unacked data, MSS/PMTU, pacing/delivery rates, acknowledged/received bytes, congestion state and algorithm-specific fields when exported |
| Queue discipline | Kind, backlog, drops | qdisc/class hierarchy, rates, backlog, overlimits, requeues, ECN-related fields where exposed; distinguish these from TCP congestion state |

Do not silently omit down interfaces, secondary addresses, non-main routing tables, IPv6, empty chains, zero counters, or unsupported nft expressions. Large set membership and driver details may load on expansion with visible counts and pagination.

### Routing view

Display policy rules in evaluation order beside searchable route tables. Selecting a route highlights its egress interface and next-hop alternatives. Include local, unreachable, prohibit, blackhole, and multipath routes.

Offer a node-side route lookup form with destination and optional source, ingress interface, mark, protocol, and ports supported by the adapter. Return supplied selectors, selected route, egress interface, preferred source, and timestamp. Missing selectors must be explicit. Distinguish route-table visualization from the result of a particular kernel lookup; neither proves delivery or firewall acceptance. `ip route get` supports lookup selectors and a `fibmatch` option, which informs the read adapter design. [ip-route manual](https://www.man7.org/linux/man-pages/man8/ip-route.8.html)

### Firewall view

Use a hook/chain flow view with family/table filtering and collapsible chains. Expand a chain into ordered rule rows, each with readable expression chips, verdict/control-flow target, optional packet/byte counters, and raw JSON. Sets/maps open in the inspector rather than expanding across the canvas.

Preserve nftables ordering and control flow: hook priority, jumps, goto, returns, policies, and NAT transformations. Acceptance by one base chain does not imply that a later chain or hook cannot drop the packet. Equal-priority ordering that cannot be established must remain ambiguous. [nftables manual](https://netfilter.org/projects/nftables/manpage.html)

An optional packet-context overlay highlights potentially relevant rules using explicit input selectors. It is an explanation of the snapshot, not a definitive packet simulator: unknown conntrack state, dynamic sets, unsupported expressions, or missing packet metadata produce “unknown/conditional.” Exact firewall attribution is not inferred from interface drop counters or rule counter changes. Never insert tracing rules, enable `nftrace`, reset counters, or modify rules to obtain visibility.

For nodes without nftables support or permission, show the precise limitation. Detect legacy firewall tooling as a capability boundary; do not imply that an empty nftables ruleset means the host has no other filtering. Legacy-rule visualization is a separate adapter extension.

### Traffic, TCP, and congestion control

- Show interface RX/TX bit rates, packet rates, drops/errors, and short history. Compute deltas using monotonic elapsed time and reset/reboot/interface identity detection. The first sample says “Waiting for second sample”; counter decreases create a gap, not a negative rate.
- Socket identity must survive endpoint reuse safely, using a kernel cookie where available. Counter deltas require the same socket identity.
- Separate host default/available congestion algorithms from the actual per-socket algorithm. Show kernel-exported congestion state and fields; do not infer BBR phases or CUBIC internals when unavailable.
- Label throughput measurements by meaning: acknowledged-byte rate, received-byte rate, delivery estimate, and pacing rate are different quantities. Keep units and sample windows visible.
- Socket details can expose cwnd, RTT, retransmission, congestion algorithm and available internal data; capability detection is necessary because fields vary across kernels. [ss manual](https://www.man7.org/linux/man-pages/man8/ss.8.html), [TCP manual](https://www.man7.org/linux/man-pages/man7/tcp.7.html)
- Interface mapping for sockets can be uncertain under policy routing, NAT, tunnels, or route changes. Mark inferred associations. Do not promise per-hop TCP throughput or protocol traffic shares from interface counters.
- Keep initial connection summaries cheap; page/filter detailed socket rows and sample only the selected/visible subset at higher frequency. Short-lived sockets may be missed by polling; explain the sampling coverage.

## 5. Progressive loading and visibility lifecycle

Treat page entry as an inspection session, not a single blocking fetch. Cached ordinary telemetry may seed the map immediately, visibly timestamped; every live subsection independently revalidates.

| Stage | Example exact operation subtitle | Immediate rendering |
| --- | --- | --- |
| 0: capabilities | `node-a · Checking network inspection capabilities in root namespace` | Cached node card, supported/disabled sections, session status |
| 1: devices | `node-a · Interfaces · RTM_GETLINK dump` | Interface cards and known link relationships; addresses/details retain loading state |
| 2: addresses and neighbors | `node-a · Addresses · RTM_GETADDR dump`; `Neighbors · RTM_GETNEIGH dump` | Address labels and neighbor cards merge into existing objects |
| 3: routing | `node-a · IPv6 routes · RTM_GETROUTE dump`; `Policy rules · RTM_GETRULE dump` | Routes, gateways, policy rows and next-hop edges appear incrementally |
| 4: firewall | `node-a · Firewall · running nft -j -a list ruleset` | Tables/chains/rules appear as validated sections arrive; unrelated layers stay usable |
| 5: connections | `node-a · TCP sockets · inet_diag dump, page 1`; `Queue disciplines · RTM_GETQDISC dump` | Socket summaries and paged rows; detailed metrics still load separately |
| 6: rates/details | `node-a · eth0 counters · collecting second sample`; `Selected TCP socket · refreshing inet_diag data` | Rates/charts and requested expensive details fill in |

These are planned adapter operations, not a promise that each kernel supports all of them. If a fallback is used, report its actual operation. Progress originates from daemon events before/after execution and during chunk delivery, not client timers that guess which command is running.

- Devices have first priority. Independent remaining stages use bounded concurrency; a slow firewall response never blocks routes or map interaction. Namespace/VRF support may require additional scoped jobs.
- Stream devices/chunks as soon as valid data is available; do not wait for every stage. If an adapter requires a whole JSON document, disclose that collection state and stream normalized sections after parsing rather than claiming per-rule kernel streaming.
- The main bar measures initial stage completion, not fictitious elapsed-time percentages. Indeterminate work shows elapsed time and rows/bytes received. Resolve skipped/unsupported/failed stages as terminal states; final status may be “Loaded with 2 unavailable sections.”
- Show the current operation subtitle and a count of other running operations. An expandable activity list includes node, namespace, stage, backend operation, queued/running/received/applied/completed status, timing, item counts, and errors. Redact sensitive values where required by access policy.
- Clicking an early interface opens its inspector immediately: identity may be ready while addresses, route membership, counters, and driver fields each say Loading. One failed section must not blank the rest.
- On refresh retain prior data marked stale until replacement sections arrive. Do not remove old objects based on an incomplete dump; reconcile deletions at the section completion boundary.
- Pausing cancels volatile subscriptions and in-flight refresh work while retaining the current snapshot with a paused timestamp. Resume opens a fresh session and resets delta baselines as needed.

### Strict page scoping

Activate only when the parent Telemetry page, selected host Network & Topology tab (or visible shared radar surface), and window visibility state permit it. Switching tabs/hosts, leaving Telemetry, closing/minimizing the window, or disposing the view cancels that session. A hidden control remaining in the visual tree does not qualify as active.

Cancellation must propagate UI → collector → daemon. Use a renewable lease so crashes and network loss also stop work. Proposed starting limits: heartbeat every 5 seconds, lease expiry after 15 seconds, normal cancellation observed within 1 second. Reference-count shared jobs across active authorized viewers; a hidden viewer releases only its own lease.

Attach a session generation to every response and ignore delayed results from earlier hosts/sessions. Clear active progress on navigation, stop child probe processes, release listeners, and unsubscribe event handlers. Reentry may show a timestamped cached snapshot immediately but starts fresh volatile queries.

Existing user-configured general telemetry collection may continue under its existing policy. This feature adds no background route/firewall/socket scans, no automatic probes, and no volatile inspection persistence through the ordinary WAL/TSDB pipeline. Persist only local layout preferences by default; keep inspection snapshots in a bounded in-memory cache.

### Proposed refresh defaults while visible

- Interface counters and selected socket metrics: 1 second; reuse existing telemetry samples when sufficient.
- Connection summaries: 5 seconds; detailed pages only while Connections or the relevant inspector is visible.
- Route/link/neighbor changes: scoped event subscriptions with coalescing where available; otherwise 10-second refresh.
- Firewall structure: page entry, explicit refresh, or a supported read-only change notification. Counter refresh at 5 seconds only while the firewall view is visible, with backoff for costly dumps.
- Driver details and large set membership: selection/expansion only. Ping/traceroute: explicit invocation only.

All cadences are tunable implementation defaults, bounded by node capability and collector limits. No overlapping dump of the same section; prioritize selection work over refreshes, back off on load, and expose delayed/stale status.

## 6. Read-only diagnostics

An interface/router inspector offers Ping, Traceroute, and Route lookup when a valid source node and destination are available. Show source node, namespace, source/interface selection, destination, address family, and probe method before Run. Choosing a remote router does not execute commands on that router; probes originate from the selected monitored node.

- Start with bounded ICMP ping and a supported bounded traceroute mode. Suggested defaults: 4 echo requests; traceroute up to 30 hops, at most 3 probes per hop, overall 30-second deadline. Expose Cancel and stream each reply/hop immediately.
- Resolve names explicitly on the source node when requested, show the resolved destination, and bind the job to that destination. Handle IPv6 link-local zone/interface requirements.
- Offer latency, loss, per-hop responses, terminal reason, and partial-result retention. A timeout must not be reported as proof of filtering.
- Use typed operation RPCs and fixed argument construction, never a remote shell string. Validate address/hostname and interface/namespace identities server-side; enforce quotas, deadlines, and output limits at the daemon as well as collector.
- Reuse node authorization and telemetry opt-in policy; advertise missing privileges/tools as unavailable. Do not silently grant privileges, install tools, change sysctls, or enable disabled collection.
- Exclude route/interface/firewall changes, counter resets, socket termination, packet injection beyond the defined probes, port scanning, and load-generating throughput tests. There is no Apply, Save rules, or generic command console.
- Record operation metadata, requester, timing, and outcome using existing audit conventions; avoid unnecessary persistence of socket owners, addresses, or complete rulesets.

## 7. Backend and desktop design

### Contract and transport work

Introduce a dedicated typed network inspection boundary, such as `INetworkInspectionProvider`, implemented by the collector-backed provider and deterministic mock fixtures. Keep inspection lifecycle separate from fleet telemetry subscriptions and configuration-write APIs.

Proposed RPC responsibilities:

1. Open/subscribe to an inspection session for a node, namespace, selected sections, and lease.
2. Stream capabilities, stage progress, snapshot chunks, entity updates/removals, section completion, and recoverable errors.
3. Request selected details, paged tables, or a section refresh.
4. Run/cancel a bounded diagnostic job and stream its observations.
5. Release the session and all associated resources.

Each event carries schema version, request/session ID, node/boot/namespace identity, section, sequence, revision, capture timestamps, freshness and completion state. Progress includes actual operation identity and execution state. Entity payloads carry provenance and raw/unknown attributes. Server-side filtering and snapshot-bound page cursors prevent duplicated/skipped rows when tables change mid-pagination.

Routes, sockets, and rules cannot be assumed to form an atomic cross-kernel snapshot. Expose per-section revisions and time windows; invalidate stale cross-links instead of presenting them as exact. Firewall handles are scoped to their ruleset revision.

The collector currently exposes query/config RPCs and telemetry streaming contracts; a complete on-demand node execution channel must be designed and verified for both push and pull deployments. Inspect existing transport semantics before choosing a versioned stream extension or a dedicated authenticated control stream. Do not assume a query RPC can already execute an arbitrary node operation. Negotiate capability/version support and preserve existing ingestion/backpressure behavior.

### Node collection

Use Linux read APIs through rtnetlink for links/addresses/routes/rules/neighbors and queue disciplines; inet_diag for TCP socket data; ethtool interfaces for supported link/driver details; nftables JSON through a fixed read-only invocation or a compatible library adapter. JSON output support is documented by the project. [nftables JSON output](https://wiki.nftables.org/wiki-nftables/index.php/Output_text_modifiers)

Prefer structured sources over parsing human-readable output. If a fallback parser is necessary, version-test it and preserve raw output. Detect capabilities per node/namespace and retain error distinctions: unsupported, permission denied, tool absent, offline, timeout, malformed data, and disabled by policy. Scope non-root namespace inspection to authorized accessible namespaces; never mutate namespace networking.

### Desktop components

- `NetworkTopologyViewModel`: active scope, selection, layer visibility, session lifecycle, and graph projection.
- `NetworkInspectionSession`: cancellation, lease, staged progress, revisions, detail requests, cache and refresh coordination.
- `TopologyCanvas`: Avalonia drawing/hit testing, pan/zoom, dragging, stable placement, edge styling, accessibility alternative.
- `NetworkObjectInspector`: independent section states and diagnostic entry points.
- `RoutesViewModel`, `FirewallViewModel`, `ConnectionsViewModel`: virtualized views over shared normalized data.
- `DiagnosticsViewModel`: typed jobs, incremental output, cancellation and result/path overlays.

Preserve existing tab/navigation identifiers initially to avoid unrelated migration. Apply granular keyed collection changes on the UI dispatcher rather than clearing entire collections on every update. Batch bursts within a short render interval without waiting for stage completion.

## 8. Scale, resilience, and validation

Proposed acceptance budgets, to validate against recorded fixtures and a representative test node:

- First device chunk drawn within 100 ms after reaching the desktop; devices remain usable while firewall/socket collection is delayed for 10 seconds.
- Drag/pan stays responsive with 200 visible graph objects; larger graphs cluster/cull, with all entities searchable in tables. Benchmark before selecting a larger visible-object budget.
- Exercise at least 1,000 interfaces, 10,000 routes, 10,000 rules, and 100,000 sockets through chunked/paged fixtures. These are benchmark cases, not silent truncation limits.
- Bound buffered events, raw payload size, cache memory, node subprocesses, and query concurrency. Announce configured limits and offer narrower scope/paging; never label truncated data complete.
- Deduplicate same-scope collection for multiple viewers while independently enforcing authorization and cancellation.

Test groups:

1. Lifecycle: hidden/construction-only views create zero new inspection jobs; visible page entry starts jobs; navigation, host changes, minimization, disposal, lost connection, and lease expiry stop them. Old results cannot contaminate the new scope.
2. Progressive UX: devices arrive first, early clicks show section placeholders, out-of-order chunks merge, partial errors remain local, empty data differs from unavailable data, and progress subtitles match daemon operations.
3. Read-only enforcement: reject unsupported operation types/arguments; verify no mutation commands are reachable, no probe starts on selection, and cancellation kills child jobs. Use isolated fixtures/namespaces for destructive test setup only.
4. Routing: IPv4/IPv6, policy rules, multiple tables, source routing, multipath, special route types, namespace collisions, deleted/reused interfaces, and incomplete dumps.
5. Firewall: hook priority, jump/goto/return, maps/sets, NAT, anonymous/named counters, absent counters, unsupported expressions, concurrent revisions, large sets, and permission failures.
6. TCP/rates: algorithm differences, missing fields, socket reuse, counter resets, first-sample gaps, clock changes, process visibility restrictions, and uncertain interface mapping.
7. UI: stable positions/selection during refresh, keyboard access, density scaling, narrow layouts, reduced motion, virtualized tables and bounded memory.
8. End-to-end: supported push/pull paths, old daemon compatibility, node offline/reconnect, simultaneous viewers, overload and slow clients. Capture actual node-side operation counts to prove hidden-page inactivity.

## 9. Implementation sequence and completion gates

| Phase | Deliverable | Completion gate |
| --- | --- | --- |
| 1. Contract and lifecycle | Capabilities, typed sessions, node transport, lease/cancellation, fixture provider | No queries while hidden; devices and progress stream end-to-end; old nodes degrade cleanly |
| 2. Map and interfaces | Shared canvas, stable drag/pin, complete interface inspector, addresses/neighbors, counters | Early interaction works before details arrive; no selection/layout reset; missing values explicit |
| 3. Routing | Policy/table views, next-hop graph, typed route lookup | Complex IPv4/IPv6 fixtures preserve all routes and explain scope/uncertainty |
| 4. Firewall | nftables snapshots, chain flow, rule/set inspectors, counter refresh | Full ordered rules remain accessible; unsupported semantics never become false verdicts |
| 5. TCP and diagnostics | Socket paging/metrics, congestion data, qdiscs, ping/traceroute overlay | Bounded explicit probes, meaningful metric labels, cancellation and identity tests pass |
| 6. Fleet reuse and hardening | Shared radar integration, large-data behavior, accessibility, documentation | No fleet-wide inspection fan-out; scale/lifecycle/read-only acceptance cases pass |

Each phase must retain progressive section loading; do not add it only after implementing monolithic queries. Phases 1–2 establish the foundation, but the requested feature is complete only after routing, firewall, diagnostics, TCP metrics, and visibility gates pass.

When implementation changes behavior, follow the repository's project-doc-sync skill and update Telemetry UI/data-contract documentation with navigation, permissions, supported capabilities, metric definitions, probe behavior, and lifecycle semantics. This planning change does not claim any feature is shipped.

## 10. Decisions to validate during implementation

- Which authenticated control transport best supports both daemon connection directions and cancellation without interfering with telemetry ingestion?
- Which structured nftables and inet_diag adapters fit the existing Go dependencies and supported Linux versions?
- Which namespace and privilege capabilities are already available under deployed service units? Unsupported scopes must stay explicitly unavailable until separately configured.
- Can read-only firewall change notifications avoid repeated full dumps on large nodes? Start with visible-page snapshot/manual refresh if not.
- Validate proposed sampling intervals, graph limits, deadlines, and lease budgets using actual node load. These defaults can change without relaxing page scoping, information completeness, or read-only behavior.
