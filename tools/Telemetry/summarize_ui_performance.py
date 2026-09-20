#!/usr/bin/env python3
"""Summarize MADTOM_UI_TIMING logs. Does not sum overlapping refresh stage durations."""
import argparse
from collections import defaultdict
import json
import math
import statistics


def summarize(lines):
    refreshes = defaultdict(list)
    metrics = defaultdict(list)
    malformed = 0
    allocated = seconds = dropped = 0
    for line in lines:
        if '[ui-performance] ' not in line:
            continue
        try:
            row = json.loads(line.split('[ui-performance] ', 1)[1])
            if row['Kind'] == 'refresh':
                refreshes[(row['Node'], row['Range'], row['Outcome'])].append(row)
            elif row['Kind'] == 'interval':
                allocated += row['AllocatedBytes']
                seconds += row['Seconds']
                dropped = max(dropped, row['DroppedRecords'])
                for name, stats in row['Metrics'].items():
                    metrics[name].append(stats)
        except (ValueError, KeyError, TypeError):
            malformed += 1
    grouped = defaultdict(list)
    from datetime import datetime
    live_bytes = stored_bytes = 0
    for line in lines:
        if '[ui-performance] ' not in line:
            continue
        try:
            row = json.loads(line.split('[ui-performance] ', 1)[1])
            if row.get('Kind') == 'interval':
                live_bytes = max(live_bytes, row.get('LiveBytes', 0))
                stored_bytes = max(stored_bytes, row.get('StoredBytes', 0))
        except (ValueError, KeyError, TypeError):
            pass

    for (node, range_text, outcome), rows in refreshes.items():
        try:
            fields = dict(p.strip().split('=', 1) for p in range_text.split(';'))
            duration = (datetime.fromisoformat(fields['end'].replace('Z', '+00:00')) -
                        datetime.fromisoformat(fields['start'].replace('Z', '+00:00'))).total_seconds()
            scope = f'{duration / 60:g} min / {fields["graphs"]} graphs'
        except (ValueError, KeyError, AttributeError):
            scope = str(range_text)
        for r in rows:
            is_zero_rpc = r.get('IsZeroRpc', r.get('RpcCount', 0) == 0)
            mode = 'zero-rpc' if is_zero_rpc else 'network'
            grouped[(node, scope, mode, outcome)].append(r)

    output = ['Refresh summaries (wall time; cancelled/error runs kept separate):']
    for (node, scope, mode, outcome), rows in sorted(grouped.items()):
        values = sorted(r['WallMs'] for r in rows)
        hits = sum(r['Cache'].get('stored-hit-decoded', 0) for r in rows)
        partial = sum(r['Cache'].get('partial-hit', 0) for r in rows)
        lock_waits = [r.get('LockWaitMs', 0) for r in rows]
        lock_holds = [r.get('LockHoldMs', 0) for r in rows]
        decoded_pts = sum(r.get('DecodedPoints', 0) for r in rows)
        extra = ''
        if any(w > 0 for w in lock_waits) or any(h > 0 for h in lock_holds):
            extra += f' lockWaitAvg={statistics.mean(lock_waits):.2f}ms lockHoldAvg={statistics.mean(lock_holds):.2f}ms'
        if decoded_pts > 0:
            extra += f' decodedPts={decoded_pts}'
        output.append(f'{node} | {scope} | {mode} | {outcome} | n={len(rows)} median={statistics.median(values):.1f}ms '
                      f'p95={values[math.ceil(len(values)*.95)-1]:.1f}ms '
                      f'RPCs={sum(r["RpcCount"] for r in rows)} storedHits={hits} partialHits={partial}{extra}')
    output.append('\nInterval metrics (max window p95 is NOT whole-run p95):')
    for name, samples in sorted(metrics.items()):
        count = sum(s['Count'] for s in samples)
        mean = sum(s['MeanMs'] * s['Count'] for s in samples) / max(1, count)
        output.append(f'{name}: n={count} mean={mean:.2f}ms maxWindowP95={max(s["P95Ms"] for s in samples):.2f}ms '
                      f'max={max(s["MaxMs"] for s in samples):.2f}ms >50ms={sum(s["Over50Ms"] for s in samples)}')
    if live_bytes > 0 or stored_bytes > 0:
        output.append(f'Peak cache occupancy: live={live_bytes / 1048576:.2f} MiB, stored={stored_bytes / 1048576:.2f} MiB')
    output.append(f'Process allocation rate: {allocated / max(seconds, .001) / 1048576:.2f} MiB/s; '
                  f'dropped output records={dropped}; malformed lines={malformed}')
    return '\n'.join(output)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('log')
    args = parser.parse_args()
    with open(args.log, encoding='utf-8', errors='replace') as stream:
        print(summarize(stream))
