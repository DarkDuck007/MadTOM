using System;
using System.Threading.Tasks;
using Grpc.Core;
using MADTOM.Identity;
using MADTOM.Protocol.Video.V1;

namespace MADTOM.Feed.Engine;

public sealed class VideoFeedIpcService : VideoFeedService.VideoFeedServiceBase
{
    private readonly FeedManager _feedManager;
    private readonly PairingServer _pairingServer;
    private readonly string _hubHost;
    private readonly int _hubPort;

    public VideoFeedIpcService(
        FeedManager feedManager,
        PairingServer pairingServer,
        string hubHost = "127.0.0.1",
        int hubPort = 7443)
    {
        _feedManager = feedManager ?? throw new ArgumentNullException(nameof(feedManager));
        _pairingServer = pairingServer ?? throw new ArgumentNullException(nameof(pairingServer));
        _hubHost = hubHost;
        _hubPort = hubPort;
    }

    public override Task<ListFeedsResponse> ListFeeds(ListFeedsRequest request, ServerCallContext context)
    {
        var res = new ListFeedsResponse();
        res.Feeds.AddRange(_feedManager.ListFeeds());
        return Task.FromResult(res);
    }

    public override Task<FeedInfo> GetFeed(GetFeedRequest request, ServerCallContext context)
    {
        var feed = _feedManager.GetFeed(request.FeedId);
        if (feed == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Feed '{request.FeedId}' not found"));
        }
        return Task.FromResult(feed.Info.Clone());
    }

    public override async Task WatchFeeds(WatchFeedsRequest request, IServerStreamWriter<FeedEvent> responseStream, ServerCallContext context)
    {
        var reader = _feedManager.SubscribeGlobalEvents();
        try
        {
            await foreach (var ev in reader.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(ev, context.CancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _feedManager.UnsubscribeGlobalEvents(reader);
        }
    }

    public override async Task WatchPreview(WatchPreviewRequest request, IServerStreamWriter<PreviewFrame> responseStream, ServerCallContext context)
    {
        var feed = _feedManager.GetFeed(request.FeedId);
        if (feed == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Feed '{request.FeedId}' not found"));
        }

        var reader = feed.SubscribePreview();
        try
        {
            await foreach (var frame in reader.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(frame, context.CancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        finally { feed.UnsubscribePreview(reader); }
    }

    public override Task<FeedInfo> CreateFeed(CreateFeedRequest request, ServerCallContext context)
    {
        var session = _feedManager.CreateObsListener(
            request.FeedId,
            request.DisplayName,
            request.CustomPassphrase,
            request.Port);

        return Task.FromResult(session.Info.Clone());
    }

    public override async Task<RemoveFeedResponse> RemoveFeed(RemoveFeedRequest request, ServerCallContext context)
    {
        bool removed = await _feedManager.RemoveFeedAsync(request.FeedId);
        return new RemoveFeedResponse
        {
            Success = removed,
            Message = removed ? "Feed removed" : "Feed not found"
        };
    }

    public override async Task<SetFeedPropertyResponse> SetFeedProperty(SetFeedPropertyRequest request, ServerCallContext context)
    {
        var feed = _feedManager.GetFeed(request.FeedId);
        if (feed == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Feed '{request.FeedId}' not found"));
        }

        if (feed.PropertySetter != null)
        {
            await feed.PropertySetter(new SetProperty
            {
                RequestId = Guid.NewGuid().ToString("N"),
                PropertyId = request.PropertyId,
                Value = request.Value,
                StringValue = request.StringValue
            });
        }

        feed.UpdateProperty(request.PropertyId, request.Value, request.StringValue);

        return new SetFeedPropertyResponse
        {
            Success = true,
            AppliedValue = request.Value,
            Message = "Property update dispatched to source."
        };
    }

    public override Task<GetPairingQrResponse> GetPairingQr(GetPairingQrRequest request, ServerCallContext context)
    {
        var session = _pairingServer.CreateSession(_hubHost, _hubPort, out var qrUri);
        return Task.FromResult(new GetPairingQrResponse
        {
            QrUri = qrUri,
            SessionId = session.SessionId,
            ComparisonCode = session.ComparisonCode,
            Token = session.Token,
            ExpiresAtUtcMs = new DateTimeOffset(session.ExpiresAtUtc).ToUnixTimeMilliseconds()
        });
    }

    public override Task<RecordLatencyTickResponse> RecordLatencyTick(RecordLatencyTickRequest request, ServerCallContext context)
    {
        var feed = _feedManager.GetFeed(request.FeedId);
        if (feed == null)
        {
            return Task.FromResult(new RecordLatencyTickResponse());
        }

        var (glassToGlass, networkRtt, ingestMs) = feed.RecordLatencyTick(request.DisplayedMillisecond);
        return Task.FromResult(new RecordLatencyTickResponse
        {
            GlassToGlassMs = glassToGlass,
            NetworkRttMs = networkRtt,
            IngestProcessingMs = ingestMs
        });
    }
}
