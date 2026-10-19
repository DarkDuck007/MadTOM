using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MADTOM.Protocol.Video.V1;

namespace MADTOM.Feed.Engine;

public sealed class FeedManager : IAsyncDisposable
{
    private readonly string _recordingsBaseDir;
    private readonly ConcurrentDictionary<string, FeedSession> _feeds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Channel<FeedEvent>> _globalSubscribers = new();
    private readonly object _lock = new();

    private int _nextPort = 8000;
    private const int MaxPort = 8100;

    public string RecordingsBaseDir => _recordingsBaseDir;

    public FeedManager(string? recordingsBaseDir = null)
    {
        _recordingsBaseDir = recordingsBaseDir ?? GetDefaultRecordingsDir();

    }

    public static string GetDefaultRecordingsDir()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(appData, "MADTOM", "recordings");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share", "madtom", "recordings");
    }

    public int AllocatePort()
    {
        lock (_lock)
        {
            for (int attempt = 0; attempt <= MaxPort - 8000; attempt++)
            {
                int allocated = _nextPort++;
                if (_nextPort > MaxPort) _nextPort = 8000;
                if (_feeds.Values.Any(f => f.Info.SrtTarget?.Port == allocated)) continue;
                try
                {
                    using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
                        System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
                    socket.ExclusiveAddressUse = true;
                    socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, allocated));
                    return allocated;
                }
                catch (System.Net.Sockets.SocketException) { }
            }
            throw new InvalidOperationException("No free video ingest port in UDP range 8000–8100.");
        }
    }

    public FeedSession CreateOrGetFeed(FeedInfo info)
    {
        lock (_lock)
        {
            if (_feeds.TryGetValue(info.FeedId, out var existing)) return existing;
            var session = new FeedSession(info, _recordingsBaseDir);
            session.Changed += BroadcastGlobalEvent;
            _feeds[info.FeedId] = session;
            BroadcastGlobalEvent(new FeedEvent
            {
                Type = FeedEvent.Types.EventType.Added,
                Feed = info.Clone()
            });
            return session;
        }
    }

    public FeedSession? GetFeed(string feedId)
    {
        _feeds.TryGetValue(feedId, out var s);
        return s;
    }

    public IReadOnlyList<FeedInfo> ListFeeds()
    {
        return _feeds.Values.Select(s => s.Info.Clone()).ToList();
    }

    public async Task<bool> RemoveFeedAsync(string feedId)
    {
        if (_feeds.TryRemove(feedId, out var session))
        {
            await session.DisposeAsync();
            BroadcastGlobalEvent(new FeedEvent
            {
                Type = FeedEvent.Types.EventType.Removed,
                Feed = session.Info.Clone()
            });
            return true;
        }
        return false;
    }

    public FeedSession CreateObsListener(string feedId, string displayName, string customPassphrase, uint port = 0)
    {
        int actualPort = port > 0 ? (int)port : AllocatePort();
        var info = new FeedInfo
        {
            FeedId = feedId,
            SourceId = "obs-" + Guid.NewGuid().ToString("N")[..6],
            DisplayName = string.IsNullOrEmpty(displayName) ? $"OBS Feed ({actualPort})" : displayName,
            IsTrusted = false, // Untrusted OBS feed (AT10)
            SourcePlatform = "obs",
            State = FeedState.Waiting,
            SrtTarget = new SrtTarget
            {
                Host = "0.0.0.0",
                Port = (uint)actualPort,
                StreamId = feedId,
                Passphrase = customPassphrase,
                LatencyMs = 200
            }
        };

        var session = CreateOrGetFeed(info);
        session.StartIngest();
        return session;
    }

    public ChannelReader<FeedEvent> SubscribeGlobalEvents()
    {
        lock (_lock)
        {
            var ch = Channel.CreateUnbounded<FeedEvent>();
            _globalSubscribers.Add(ch);
            // Send current snapshot
            foreach (var feed in _feeds.Values)
            {
                ch.Writer.TryWrite(new FeedEvent
                {
                    Type = FeedEvent.Types.EventType.Added,
                    Feed = feed.Info.Clone()
                });
            }
            return ch.Reader;
        }
    }

    public void UnsubscribeGlobalEvents(ChannelReader<FeedEvent> reader)
    {
        lock (_lock)
        {
            _globalSubscribers.RemoveAll(ch => ch.Reader == reader);
        }
    }

    private void BroadcastGlobalEvent(FeedEvent ev)
    {
        lock (_lock)
        {
            for (int i = _globalSubscribers.Count - 1; i >= 0; i--)
            {
                if (!_globalSubscribers[i].Writer.TryWrite(ev))
                {
                    _globalSubscribers.RemoveAt(i);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var feed in _feeds.Values)
        {
            await feed.DisposeAsync();
        }
        _feeds.Clear();
    }
}
