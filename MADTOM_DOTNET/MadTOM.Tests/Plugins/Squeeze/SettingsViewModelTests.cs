using System.Threading.Tasks;
using SQUEEZE.Services;
using SQUEEZE.ViewModels;
using Xunit;

namespace SQUEEZE.Tests;

public class SettingsViewModelTests
{
    [Fact]
    public async Task TestConnection_SetsSuccessStatus()
    {
        var backend = new MockTranscoderBackendService();
        var discovery = new MdnsServerDiscoveryService();
        var vm = new SettingsViewModel(backend, discovery);

        vm.ServerUrl = "http://127.0.0.1:8080";
        await vm.TestConnection();

        Assert.Contains("successful", vm.StatusMessage);
        Assert.Equal("#3FB950", vm.StatusColor);
    }

    [Fact]
    public async Task Connect_TriggersCallbackAndSavesUrl()
    {
        var backend = new MockTranscoderBackendService();
        var discovery = new MdnsServerDiscoveryService();
        var vm = new SettingsViewModel(backend, discovery);

        bool callbackInvoked = false;
        vm.OnConnectionChanged = (connected) => callbackInvoked = connected;
        vm.ServerUrl = "http://192.168.1.50:8080";

        await vm.Connect();

        Assert.True(callbackInvoked);
        Assert.Equal("http://192.168.1.50:8080", backend.BaseUrl);
        Assert.Contains("Connected", vm.StatusMessage);
    }

    [Fact]
    public async Task SelectAndConnect_UpdatesServerUrlAndConnects()
    {
        var backend = new MockTranscoderBackendService();
        var discovery = new MdnsServerDiscoveryService();
        var vm = new SettingsViewModel(backend, discovery);

        var server = new DiscoveredServer
        {
            NodeId = "TEST_NODE",
            Host = "10.0.0.5",
            Port = 8080
        };

        await vm.SelectAndConnect(server);

        Assert.Equal("http://10.0.0.5:8080", vm.ServerUrl);
        Assert.True(backend.IsConnected);
    }
}

