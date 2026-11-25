using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MADTOM.Plugins.SlideForge.Services;

namespace MADTOM.Plugins.SlideForge.ViewModels;

public sealed partial class SfConfigViewModel : ObservableObject
{
    private SfRpc? _rpc;
    private int _refreshInFlight;

    [ObservableProperty] private string _configJson = "{}";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorMessage;

    public SfConfigViewModel(SfRpc? rpc)
    {
        _rpc = rpc;
    }

    public void SetRpc(SfRpc? rpc) => _rpc = rpc;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_rpc is null)
        {
            ErrorMessage = "not_connected";
            return;
        }

        IsBusy = true;
        try
        {
            var root = await _rpc.CallAsync("get_config", null, ct);
            string pretty = SfJsonHelpers.PrettyPrint(root);
            if (!string.Equals(ConfigJson, pretty, StringComparison.Ordinal))
                ConfigJson = pretty;
            ErrorMessage = null;
        }
        catch (SfRpcException ex)
        {
            ErrorMessage = ex.Code;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RefreshSilentAsync(CancellationToken ct = default)
    {
        if (_rpc is null)
        {
            ErrorMessage = "not_connected";
            return;
        }

        if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0)
            return;

        try
        {
            var root = await _rpc.CallAsync("get_config", null, ct);
            string pretty = SfJsonHelpers.PrettyPrint(root);
            if (!string.Equals(ConfigJson, pretty, StringComparison.Ordinal))
                ConfigJson = pretty;
            ErrorMessage = null;
        }
        catch (SfRpcException ex)
        {
            ErrorMessage = ex.Code;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInFlight, 0);
        }
    }
}
