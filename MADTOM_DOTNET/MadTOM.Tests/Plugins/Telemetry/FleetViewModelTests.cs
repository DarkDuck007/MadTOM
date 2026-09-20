using System.Linq;
using MadTOM.Services;
using MadTOM.ViewModels;
using Xunit;

namespace MadTOM.Tests;

public class FleetViewModelTests
{
    private readonly MockTelemetryDataProvider _dataProvider;

    public FleetViewModelTests()
    {
        _dataProvider = new MockTelemetryDataProvider(startBackgroundTimer: false);
    }

    [Fact]
    public void FleetViewModel_LoadsInitialNodes()
    {
        var vm = new FleetViewModel(_dataProvider);
        Assert.NotEmpty(vm.Cards);
        Assert.Equal(6, vm.Cards.Count);
    }

    [Fact]
    public void FleetViewModel_FiltersByRoleBaremetal()
    {
        var vm = new FleetViewModel(_dataProvider);
        vm.SetFilter("baremetal");

        Assert.NotEmpty(vm.Cards);
        Assert.All(vm.Cards, card => Assert.Equal("baremetal", card.Node.Role, ignoreCase: true));
    }

    [Fact]
    public void FleetViewModel_FiltersByRoleVm()
    {
        var vm = new FleetViewModel(_dataProvider);
        vm.SetFilter("vm");

        Assert.NotEmpty(vm.Cards);
        Assert.All(vm.Cards, card => Assert.Equal("vm", card.Node.Role, ignoreCase: true));
    }

    [Fact]
    public void FleetViewModel_FiltersBySearchText()
    {
        var vm = new FleetViewModel(_dataProvider);
        vm.SearchText = "gander-epyc-01";

        Assert.Single(vm.Cards);
        Assert.Equal("gander-epyc-01", vm.Cards.First().Node.Id);
    }

    [Fact]
    public void FleetViewModel_FiresOpenDetailRequested()
    {
        var vm = new FleetViewModel(_dataProvider);
        string? requestedHost = null;
        vm.OpenHostDetailRequested += hostId => requestedHost = hostId;

        var firstCard = vm.Cards.First();
        firstCard.OpenDetailCommand.Execute(null);

        Assert.Equal(firstCard.Node.Id, requestedHost);
    }

    [Fact]
    public void FleetViewModel_ApplyFilter_PreservesCardInstancesOnUpdate()
    {
        var vm = new FleetViewModel(_dataProvider);
        Assert.NotEmpty(vm.Cards);
        var initialCards = vm.Cards.ToList();
        var firstCard = initialCards[0];

        // Trigger filter reapplication
        vm.ApplyFilter();

        // Cards list should retain the exact same card instances
        Assert.Equal(initialCards.Count, vm.Cards.Count);
        Assert.Same(firstCard, vm.Cards[0]);
    }
}
