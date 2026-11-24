using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using MADTOM.Plugins.SlideForge.Proto.V1;

namespace MADTOM.Plugins.SlideForge.Services;

public sealed class SfRpcException : Exception
{
    public string Code { get; }

    public SfRpcException(string code, string? message = null, Exception? innerException = null)
        : base(string.IsNullOrWhiteSpace(message) ? code : $"{code}: {message}", innerException)
    {
        Code = code;
    }
}

/// <summary>
/// Allowlisted JSON-over-gRPC client wrapper for SlideForgeControlService.Call.
/// </summary>
public sealed class SfRpc
{
    private readonly SlideForgeControlService.SlideForgeControlServiceClient _client;
    private readonly string _nodeId;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly HashSet<string> AllowlistedMethods = new(StringComparer.Ordinal)
    {
        "get_overview",
        "list_jobs",
        "get_job",
        "list_job_files",
        "cancel_job",
        "extend_job",
        "get_job_file",
        "get_job_transcript",
        "list_users",
        "get_user",
        "set_user_quota",
        "set_user_banned",
        "list_guides",
        "get_guide",
        "put_guide",
        "list_tips",
        "upsert_tip",
        "delete_tip",
        "preview_prompt",
        "get_config",
        "sandbox_list_vms",
        "sandbox_stats",
        "sandbox_vm_action",
        "sandbox_list_files",
        "sandbox_read_text",
        "sandbox_preview",
        "sandbox_backups",
        "sandbox_restore_backup",
        "sandbox_library_requests",
        "sandbox_decide_library",
        "sandbox_image_status",
        "sandbox_rebuild_image",
        "sandbox_runs",
    };

    public static IReadOnlySet<string> Methods => AllowlistedMethods;

    public string NodeId => _nodeId;

    public SfRpc(SlideForgeControlService.SlideForgeControlServiceClient client, string nodeId)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _nodeId = nodeId ?? string.Empty;
    }

    public async Task<JsonElement> CallAsync(
        string method,
        object? payload = null,
        CancellationToken ct = default,
        int timeoutMs = 30000)
    {
        if (string.IsNullOrWhiteSpace(method) || !AllowlistedMethods.Contains(method))
            throw new SfRpcException("method_not_allowed");

        int effectiveTimeout = timeoutMs <= 0 ? 30000 : Math.Clamp(timeoutMs, 1, 120000);
        string payloadJson = payload is null ? "{}" : JsonSerializer.Serialize(payload, SerializerOptions);

        var request = new SfCallRequest
        {
            NodeId = _nodeId,
            Method = method,
            PayloadJson = payloadJson,
            TimeoutMs = (uint)effectiveTimeout,
        };

        SfCallResponse response;
        try
        {
            response = await _client.CallAsync(
                request,
                deadline: DateTime.UtcNow.AddMilliseconds(effectiveTimeout + 5000),
                cancellationToken: ct).ResponseAsync.ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            throw new SfRpcException("unavailable", ex.Status.Detail, ex);
        }

        if (!response.Ok)
        {
            string code = string.IsNullOrWhiteSpace(response.Error) ? "rpc_error" : response.Error;
            throw new SfRpcException(code);
        }

        string responseJson = string.IsNullOrWhiteSpace(response.PayloadJson) ? "{}" : response.PayloadJson;
        using var doc = JsonDocument.Parse(responseJson);
        return doc.RootElement.Clone();
    }
}
