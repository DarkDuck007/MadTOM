using System;
using System.Threading;
using System.Threading.Tasks;

namespace MADTOM.Plugins.NoxAI.ViewModels;

public sealed partial class RawLogsApiTabViewModel
{
    private readonly object _historyLoadGate = new();
    private bool _disposed;
    private bool _historyRecoveryNeeded = true;
    private int _recoveryRevision;
    private Task? _historyRecoveryTask;

    private void ResetConnectionRecovery()
    {
        lock (_historyLoadGate)
        {
            MarkHistoryRecoveryNeeded();
            _historyRecoveryTask = null;
            _apiListTask = null;
            _logDiscoveryTask = null;
        }
    }

    public void MarkHistoryRecoveryNeeded()
    {
        lock (_historyLoadGate)
        {
            _historyRecoveryNeeded = true;
            ++_recoveryRevision;
        }
    }

    // Called on the UI thread after a successful status poll or stream handshake.
    // Status polling also covers idle streams that have not sent response headers yet.
    public Task RecoverHistoryAfterConnectionAsync(bool force = false)
    {
        lock (_historyLoadGate)
        {
            if (_disposed || SelectedNode == null) return Task.CompletedTask;
            if (force) MarkHistoryRecoveryNeeded();
            if (_historyRecoveryTask is { IsCompleted: false }) return _historyRecoveryTask;
            if (!_historyRecoveryNeeded) return Task.CompletedTask;
            var node = SelectedNode;
            var token = _historyCts.Token;
            int revision = _recoveryRevision;
            return _historyRecoveryTask = RecoverAsync();

            async Task RecoverAsync()
            {
                try
                {
                    // Let selection/settings settle before issuing RPCs from a constructor.
                    await Task.Yield();
                    bool IsCurrent() => !_disposed && !token.IsCancellationRequested && SelectedNode == node;
                    if (!IsCurrent()) return;
                    // A failed config sync must never suppress independently readable history.
                    bool configured = await SyncNodeConfigAsync(node, token);
                    if (!IsCurrent()) return;
                    bool logs = await LoadLogHistoryCoreAsync();
                    if (!IsCurrent()) return;
                    bool calls = node.LogMode == MADTOM.Plugins.Telemetry.Proto.V1.TelemetryOptInMode.OptInOff
                        || await LoadApiCallListCoreAsync();
                    if (!IsCurrent()) return;
                    bool chats = await LoadChatRegistryCoreAsync();
                    if (!IsCurrent()) return;
                    bool trace = true;
                    lock (_inflightLogChunks) _chunkFetchFailures.Clear();
                    CacheRevision++;
                    if (IsChatsViewActive)
                    {
                        CancelChatLoads();
                        _chatTraceSupported = null;
                        RebuildChatHistory(resetLimit: false);
                        if (SelectedChatGroup?.ChatId is long chatId)
                            trace = await LoadChatTraceAsync(chatId, _chatCts.Token);
                    }
                    if (!IsCurrent()) return;
                    if (IsTabActive && SelectedSubViewIndex == 1 && SelectedApiCall != null)
                        EnsureDetailsLoaded(SelectedApiCall);
                    await RefreshSpoolStatus();
                    lock (_historyLoadGate)
                    {
                        if (IsCurrent() && revision == _recoveryRevision)
                            _historyRecoveryNeeded = !(configured && logs && calls && chats && trace);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception) { /* The next successful status poll retries incomplete recovery. */ }
            }
        }
    }
}
