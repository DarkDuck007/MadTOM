using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using MADTOM.Plugins.NoxAI.Proto.V1;
using MADTOM.Plugins.NoxAI.Services;

namespace MADTOM.Plugins.NoxAI.ViewModels;

public sealed class AgyRebuildReasonRow
{
    public string Code { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public long Count { get; init; }
}

public sealed class AgySessionRow
{
    public long ChatId { get; init; }
    public string ChatTitle { get; init; } = string.Empty;
    public long Epoch { get; init; }
    public long Turns { get; init; }
    public long Tokens { get; init; }
    public long BootstrapTokens { get; init; }
    public long RebuildAtTokens { get; init; }
    public double FillFraction { get; init; }
    public string SizeText { get; init; } = string.Empty;
    public string CacheHitText { get; init; } = string.Empty;
    public string GrowthText { get; init; } = string.Empty;
    public string IdleText { get; init; } = string.Empty;
    public string StartedReasonText { get; init; } = string.Empty;
    public bool WillRebuild { get; init; }
    public string NextTurnText { get; init; } = string.Empty;

    public static AgySessionRow FromProto(NoxAgySession session)
    {
        long denom = session.RebuildAtTokens - session.BootstrapTokens;
        double fillFraction = denom <= 0
            ? 0.0
            : Math.Clamp((double)(session.Tokens - session.BootstrapTokens) / denom, 0.0, 1.0);

        string nextReason = session.NextTurnRebuilds?.Trim() ?? string.Empty;
        bool willRebuild = !string.IsNullOrEmpty(nextReason);

        return new AgySessionRow
        {
            ChatId = session.ChatId,
            ChatTitle = string.IsNullOrWhiteSpace(session.ChatTitle)
                ? session.ChatId.ToString(CultureInfo.InvariantCulture)
                : session.ChatTitle,
            Epoch = session.Epoch,
            Turns = session.Turns,
            Tokens = session.Tokens,
            BootstrapTokens = session.BootstrapTokens,
            RebuildAtTokens = session.RebuildAtTokens,
            FillFraction = fillFraction,
            SizeText = $"{AgySessionsTabViewModel.FormatTokens(session.Tokens)} / {AgySessionsTabViewModel.FormatTokens(session.RebuildAtTokens)}",
            CacheHitText = $"{Math.Round(Math.Clamp(session.CacheHit, 0.0, 1.0) * 100.0).ToString("0", CultureInfo.InvariantCulture)}%",
            GrowthText = $"{AgySessionsTabViewModel.FormatTokens(session.GrowthPerTurn)}/turn",
            IdleText = AgySessionsTabViewModel.FormatIdleDuration(session.IdleSeconds),
            StartedReasonText = AgySessionsTabViewModel.FormatReason(session.StartedReason),
            WillRebuild = willRebuild,
            NextTurnText = willRebuild ? AgySessionsTabViewModel.FormatReason(nextReason) : string.Empty
        };
    }
}

public sealed partial class AgySessionsTabViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(10);

    private static readonly (string Code, string Label, string ShortLabel)[] ReasonDefinitions =
    {
        ("new", "First turn since NoxChat start", "new"),
        ("growth", "Token ceiling", "growth"),
        ("cold", "Idle (cache expired)", "idle"),
        ("max_turns", "Turn limit", "max turns"),
        ("expired", "Bridge lost session", "expired"),
        ("reset", "Reset (/clear)", "reset")
    };

    private readonly NoxNodeManager _nodeManager;
    private readonly object _timerLock = new();
    private Timer? _autoRefreshTimer;
    private bool _disposed;
    private bool _nodeExplicitlyCleared;
    private int _refreshInFlight;

    [ObservableProperty] private NoxNodeModel? _selectedNode;
    [ObservableProperty] private bool _isTabActive;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = string.Empty;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHistoryMode))]
    private string _historyMode = string.Empty;

    public bool HasHistoryMode => !string.IsNullOrWhiteSpace(HistoryMode);

    [ObservableProperty] private string _rebuildSummary = "—";
    [ObservableProperty] private string _tokenSummary = "—";
    [ObservableProperty] private string _settingsSummary = "—";

    public ObservableCollection<AgySessionRow> Sessions { get; } = new();
    public ObservableCollection<AgyRebuildReasonRow> RebuildReasons { get; } = new();

    public bool HasSessions => Sessions.Count > 0;

    public AgySessionsTabViewModel(NoxNodeManager nodeManager)
    {
        _nodeManager = nodeManager;
        PopulateEmptyRebuildReasons();
        Sessions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSessions));
    }

    public static string FormatTokens(long tokens)
    {
        if (Math.Abs(tokens) < 1000)
        {
            return tokens.ToString(CultureInfo.InvariantCulture);
        }

        return (tokens / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k";
    }

    public static string FormatTokens(double tokens)
    {
        if (Math.Abs(tokens) < 1000.0)
        {
            return Math.Round(tokens).ToString("0", CultureInfo.InvariantCulture);
        }

        return (tokens / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k";
    }

    private static string FormatCompactTokens(long tokens)
    {
        if (Math.Abs(tokens) < 1000)
        {
            return tokens.ToString(CultureInfo.InvariantCulture);
        }

        return (tokens / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
    }

    public static string FormatIdleDuration(long seconds)
    {
        long clamped = Math.Max(0, seconds);
        if (clamped < 60)
        {
            return $"{clamped.ToString(CultureInfo.InvariantCulture)} s";
        }

        if (clamped < 3600)
        {
            return $"{(clamped / 60).ToString(CultureInfo.InvariantCulture)} min";
        }

        return $"{(clamped / 3600.0).ToString("0.0", CultureInfo.InvariantCulture)} h";
    }

    public static string FormatReason(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return string.Empty;
        }

        string normalized = code.Trim();
        return normalized switch
        {
            "new" => "First turn since NoxChat start",
            "growth" => "Token ceiling",
            "cold" => "Idle (cache expired)",
            "max_turns" => "Turn limit",
            "expired" => "Bridge lost session",
            "reset" => "Reset (/clear)",
            _ => normalized
        };
    }

    partial void OnSelectedNodeChanged(NoxNodeModel? value)
    {
        if (value == null)
        {
            _nodeExplicitlyCleared = true;
            UpdateAutoRefreshTimer();
            _ = DispatchUiAsync(() =>
            {
                Sessions.Clear();
                PopulateEmptyRebuildReasons();
                HistoryMode = string.Empty;
                RebuildSummary = "—";
                TokenSummary = "—";
                SettingsSummary = "—";
                ErrorText = string.Empty;
                StatusText = "No node selected";
            });
        }
        else
        {
            _nodeExplicitlyCleared = false;
            UpdateAutoRefreshTimer();
            if (IsTabActive)
            {
                _ = RefreshAsync();
            }
        }
    }

    partial void OnIsTabActiveChanged(bool value)
    {
        UpdateAutoRefreshTimer();
        if (value && GetActiveNode() != null)
        {
            _ = RefreshAsync();
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var node = GetActiveNode();
        if (node == null) return;

        if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0)
        {
            return;
        }

        await DispatchUiAsync(() => IsLoading = true);
        try
        {
            var client = _nodeManager.GetClient(node);
            var resp = await client.GetAgySessionsAsync(new NoxAgySessionsRequest()).ResponseAsync;
            var now = DateTimeOffset.Now;

            await DispatchUiAsync(() => ApplyResponse(resp, now));
        }
        catch (RpcException ex)
        {
            string msg = FormatRpcError(ex);
            await DispatchUiAsync(() =>
            {
                ErrorText = msg;
                StatusText = msg;
            });
        }
        catch (Exception ex)
        {
            await DispatchUiAsync(() =>
            {
                ErrorText = ex.Message;
                StatusText = ex.Message;
            });
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInFlight, 0);
            await DispatchUiAsync(() => IsLoading = false);
        }
    }

    public void ApplyResponse(NoxAgySessionsResponse resp, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(resp);

        HistoryMode = resp.HistoryMode?.Trim() ?? string.Empty;

        var counts = resp.Rebuilds;
        long[] countValues =
        {
            counts?.New ?? 0,
            counts?.Growth ?? 0,
            counts?.Cold ?? 0,
            counts?.MaxTurns ?? 0,
            counts?.Expired ?? 0,
            counts?.Reset ?? 0
        };

        RebuildReasons.Clear();
        for (int i = 0; i < ReasonDefinitions.Length; i++)
        {
            RebuildReasons.Add(new AgyRebuildReasonRow
            {
                Code = ReasonDefinitions[i].Code,
                Label = ReasonDefinitions[i].Label,
                Count = countValues[i]
            });
        }

        RebuildSummary = BuildRebuildSummary(countValues, resp.SinceUnix, now);
        TokenSummary = BuildTokenSummary(resp.InputTokens, resp.WarmCacheHit);
        SettingsSummary = BuildSettingsSummary(resp.Settings);

        Sessions.Clear();
        foreach (var session in resp.Sessions)
        {
            Sessions.Add(AgySessionRow.FromProto(session));
        }

        if (!string.IsNullOrWhiteSpace(resp.Error))
        {
            ErrorText = resp.Error;
            StatusText = resp.Error;
        }
        else
        {
            ErrorText = string.Empty;
            string modeSuffix = string.IsNullOrEmpty(HistoryMode) ? string.Empty : $" ({HistoryMode})";
            StatusText = $"Loaded {Sessions.Count} active session{(Sessions.Count == 1 ? "" : "s")}{modeSuffix} · updated {now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}";
        }
    }

    private static string BuildRebuildSummary(long[] countValues, long sinceUnix, DateTimeOffset now)
    {
        long total = 0;
        for (int i = 0; i < countValues.Length; i++)
        {
            total += countValues[i];
        }

        string noun = total == 1 ? "rebuild" : "rebuilds";
        string header = sinceUnix > 0
            ? $"{total.ToString(CultureInfo.InvariantCulture)} {noun} since {DateTimeOffset.FromUnixTimeSeconds(sinceUnix).ToOffset(now.Offset).ToString("HH:mm", CultureInfo.InvariantCulture)}"
            : $"{total.ToString(CultureInfo.InvariantCulture)} {noun}";

        var nonZero = new List<(string ShortLabel, long Count, int Index)>();
        for (int i = 0; i < ReasonDefinitions.Length; i++)
        {
            if (countValues[i] > 0)
            {
                nonZero.Add((ReasonDefinitions[i].ShortLabel, countValues[i], i));
            }
        }

        if (nonZero.Count == 0)
        {
            return header;
        }

        nonZero.Sort((a, b) =>
        {
            int cmp = b.Count.CompareTo(a.Count);
            return cmp != 0 ? cmp : a.Index.CompareTo(b.Index);
        });

        string breakdown = string.Join(" · ", nonZero.Select(x => $"{x.ShortLabel} {x.Count.ToString(CultureInfo.InvariantCulture)}"));
        return $"{header} — {breakdown}";
    }

    private static string BuildTokenSummary(NoxAgyInputTokensByKind? inputTokens, double warmCacheHit)
    {
        long warmUncached = inputTokens?.Warm?.Uncached ?? 0;
        long warmCached = inputTokens?.Warm?.Cached ?? 0;
        long bootUncached = inputTokens?.Bootstrap?.Uncached ?? 0;
        long bootCalls = inputTokens?.Bootstrap?.Calls ?? 0;

        // Share of warm input actually served from cache (the totals), else the tracker's moving average
        double hit = warmUncached + warmCached > 0
            ? (double)warmCached / (warmUncached + warmCached)
            : warmCacheHit;
        string hitPct = Math.Round(Math.Clamp(hit, 0.0, 1.0) * 100.0)
            .ToString("0", CultureInfo.InvariantCulture);
        string callWord = bootCalls == 1 ? "call" : "calls";

        return $"Warm: {FormatTokens(warmUncached)} uncached · {FormatTokens(warmCached)} cached ({hitPct}% hit) · Bootstrap: {FormatTokens(bootUncached)} uncached over {bootCalls.ToString(CultureInfo.InvariantCulture)} {callWord}";
    }

    private static string BuildSettingsSummary(NoxAgySessionSettings? settings)
    {
        if (settings == null)
        {
            return "No settings reported";
        }

        string growthMode = settings.RebaseGrowthTokens?.Trim() ?? string.Empty;
        string growthPart;
        if (string.Equals(growthMode, "auto", StringComparison.OrdinalIgnoreCase))
        {
            growthPart = settings.RebaseGrowthMinTokens > 0 || settings.RebaseGrowthMaxTokens > 0
                ? $"Rebuild after +auto growth ({FormatCompactTokens(settings.RebaseGrowthMinTokens)}–{FormatCompactTokens(settings.RebaseGrowthMaxTokens)})"
                : "Rebuild after +auto growth";
        }
        else if (long.TryParse(growthMode, NumberStyles.Integer, CultureInfo.InvariantCulture, out long fixedTokens))
        {
            growthPart = $"Rebuild after +{FormatCompactTokens(fixedTokens)} growth";
        }
        else if (!string.IsNullOrEmpty(growthMode))
        {
            growthPart = $"Rebuild after +{growthMode} growth";
        }
        else
        {
            growthPart = "Rebuild growth: default";
        }

        string idlePart = settings.ColdRebuildAfterSeconds > 0
            ? $"idle {settings.ColdRebuildAfterSeconds.ToString(CultureInfo.InvariantCulture)} s"
            : "idle off";

        string turnLimitPart = settings.MaxSessionTurns > 0
            ? $"turn limit {settings.MaxSessionTurns.ToString(CultureInfo.InvariantCulture)}"
            : "turn limit off";

        string bootstrapPart = settings.WorkingDialogueTurns > 0
            ? $"bootstrap = {settings.WorkingDialogueTurns.ToString(CultureInfo.InvariantCulture)} messages"
            : "bootstrap = default";

        return $"{growthPart} · {idlePart} · {turnLimitPart} · {bootstrapPart}";
    }

    private void PopulateEmptyRebuildReasons()
    {
        RebuildReasons.Clear();
        foreach (var def in ReasonDefinitions)
        {
            RebuildReasons.Add(new AgyRebuildReasonRow
            {
                Code = def.Code,
                Label = def.Label,
                Count = 0
            });
        }
    }

    private void UpdateAutoRefreshTimer()
    {
        lock (_timerLock)
        {
            bool shouldRun = !_disposed && IsTabActive && SelectedNode != null;
            if (shouldRun)
            {
                _autoRefreshTimer ??= new Timer(_ => OnAutoRefreshTick(), null, AutoRefreshInterval, AutoRefreshInterval);
            }
            else
            {
                _autoRefreshTimer?.Dispose();
                _autoRefreshTimer = null;
            }
        }
    }

    private void OnAutoRefreshTick()
    {
        if (_disposed || !IsTabActive || SelectedNode == null)
        {
            return;
        }

        _ = RefreshAsync();
    }

    private NoxNodeModel? GetActiveNode()
    {
        if (SelectedNode != null) return SelectedNode;
        if (_nodeExplicitlyCleared) return null;
        return _nodeManager.Nodes.FirstOrDefault();
    }

    private static string FormatRpcError(RpcException ex) =>
        !string.IsNullOrWhiteSpace(ex.Status.Detail) ? ex.Status.Detail : ex.Message;

    private static Task DispatchUiAsync(Action action)
    {
        if (Application.Current == null || Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    public void Dispose()
    {
        lock (_timerLock)
        {
            _disposed = true;
            _autoRefreshTimer?.Dispose();
            _autoRefreshTimer = null;
        }
    }
}
