using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using MADTOM.Plugins.SlideForge.Proto.V1;

namespace MADTOM.Tests.Plugins.SlideForge;

/// <summary>Stand-in for the slideforge-probe: answers <c>Call</c> from per-method handlers and records requests.</summary>
internal sealed class FakeSlideForgeClient : SlideForgeControlService.SlideForgeControlServiceClient
{
    public List<(string Method, JsonNode? Payload, uint TimeoutMs, string NodeId)> Calls { get; } = new();
    public Dictionary<string, Func<JsonNode?, object>> Handlers { get; } = new();
    /// <summary>method → error code returned as ok=false.</summary>
    public Dictionary<string, string> Errors { get; } = new();
    public bool Offline { get; set; }

    public JsonNode? LastPayload(string method) => Calls.Last(c => c.Method == method).Payload;
    public int Count(string method) => Calls.Count(c => c.Method == method);

    public override AsyncUnaryCall<SfCallResponse> CallAsync(
        SfCallRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
    {
        var payload = string.IsNullOrEmpty(request.PayloadJson) ? null : JsonNode.Parse(request.PayloadJson);
        Calls.Add((request.Method, payload, request.TimeoutMs, request.NodeId));
        if (Offline)
            return new AsyncUnaryCall<SfCallResponse>(
                Task.FromException<SfCallResponse>(new RpcException(new Status(StatusCode.Unavailable, "probe offline"))),
                Task.FromResult(new Metadata()), () => new Status(StatusCode.Unavailable, "probe offline"), () => new Metadata(), () => { });
        SfCallResponse res;
        if (Errors.TryGetValue(request.Method, out var code))
            res = new SfCallResponse { Ok = false, Error = code };
        else if (Handlers.TryGetValue(request.Method, out var h))
            res = new SfCallResponse { Ok = true, PayloadJson = JsonSerializer.Serialize(h(payload)) };
        else
            res = new SfCallResponse { Ok = true, PayloadJson = "{}" };
        return new AsyncUnaryCall<SfCallResponse>(Task.FromResult(res), Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }
}
