using System;
using System.IO;
using Avalonia.Controls;
using MADTOM.Console.Hosting;
using MADTOM.Console.ViewModels;
using MADTOM.PluginContracts.Controls;
using Xunit;

namespace MadTOM.Tests;

public class UniversalModalAndDeviceControlTests
{
    [Fact]
    public void UniversalModalControl_DefaultsAndProperties_WorkProperly()
    {
        var modal = new UniversalModalControl
        {
            Title = "Settings",
            Subtitle = "System settings",
            Icon = "⚙",
            DialogWidth = 720,
            DialogHeight = 600,
            DialogMinWidth = 500,
            DialogMinHeight = 350
        };

        Assert.False(modal.IsOpen);
        Assert.False(modal.IsVisible);
        Assert.Equal("Settings", modal.Title);
        Assert.Equal("System settings", modal.Subtitle);
        Assert.Equal("⚙", modal.Icon);
        Assert.Equal(720, modal.DialogWidth);
        Assert.Equal(600, modal.DialogHeight);

        modal.IsOpen = true;
        Assert.True(modal.IsVisible);

        bool closeInvoked = false;
        modal.CloseCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => closeInvoked = true);
        modal.RequestClose();

        Assert.True(closeInvoked);
    }

    [Fact]
    public void CenteredDialogResizer_AttachDoesNotThrow()
    {
        var dialog = new Border { Width = 600, Height = 400 };
        var left = new Border();
        var right = new Border();
        var top = new Border();
        var bottom = new Border();
        var topLeft = new Border();
        var topRight = new Border();
        var bottomLeft = new Border();
        var bottomRight = new Border();
        var touchGrip = new Border();

        CenteredDialogResizer.Attach(
            dialog,
            left, right, top, bottom,
            topLeft, topRight, bottomLeft, bottomRight,
            touchGrip: touchGrip,
            minWidth: 400, minHeight: 300);

        Assert.Equal(600, dialog.Width);
        Assert.Equal(400, dialog.Height);
    }

    [Fact]
    public void LinuxBrightnessService_DiscoversDevicesAndReadsValues()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "madtom_test_backlight_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Device 1: eDP primary
            string dev1 = Path.Combine(tempDir, "intel_backlight");
            Directory.CreateDirectory(dev1);
            File.WriteAllText(Path.Combine(dev1, "max_brightness"), "500\n");
            File.WriteAllText(Path.Combine(dev1, "brightness"), "250\n");

            // Device 2: External secondary
            string dev2 = Path.Combine(tempDir, "card0-DP-1");
            Directory.CreateDirectory(dev2);
            File.WriteAllText(Path.Combine(dev2, "max_brightness"), "100\n");
            File.WriteAllText(Path.Combine(dev2, "brightness"), "75\n");

            var service = new LinuxBrightnessService(tempDir);
            var devices = service.GetDevices();

            Assert.Equal(2, devices.Count);

            var d1 = Assert.Single(devices, d => d.Id == "intel_backlight");
            Assert.Equal(500, d1.MaxBrightness);
            Assert.Equal(250, d1.CurrentBrightness);
            Assert.Equal(50.0, d1.BrightnessPercent);
            Assert.Equal(25, d1.MinSafeBrightness); // 5% of 500 = 25

            var d2 = Assert.Single(devices, d => d.Id == "card0-DP-1");
            Assert.Equal(100, d2.MaxBrightness);
            Assert.Equal(75, d2.CurrentBrightness);
            Assert.Equal(75.0, d2.BrightnessPercent);
            Assert.Equal(5, d2.MinSafeBrightness); // 5% of 100 = 5

            // Test writing brightness
            bool success = service.SetBrightness(d1, 300);
            Assert.True(success);
            Assert.Equal(300, d1.CurrentBrightness);
            Assert.Equal("300", File.ReadAllText(Path.Combine(dev1, "brightness")).Trim());
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void DisplayDeviceItemViewModel_SafeguardTriggersBelow5Percent()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "madtom_test_safeguard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string devDir = Path.Combine(tempDir, "intel_backlight");
            Directory.CreateDirectory(devDir);
            File.WriteAllText(Path.Combine(devDir, "max_brightness"), "1000\n");
            File.WriteAllText(Path.Combine(devDir, "brightness"), "500\n");

            var service = new LinuxBrightnessService(tempDir);
            var device = service.GetDevices()[0];

            bool safeguardTriggered = false;
            int requestedRaw = 0;
            double requestedPercent = 0;

            var vm = new DisplayDeviceItemViewModel(
                device,
                service,
                (d, raw, pct) =>
                {
                    safeguardTriggered = true;
                    requestedRaw = raw;
                    requestedPercent = pct;
                });

            // 5% of 1000 = 50. Setting to 10% (100) is safe and applies immediately
            vm.SliderPercent = 10;
            vm.ApplySliderBrightness();

            Assert.False(safeguardTriggered);
            Assert.Equal(100, vm.CurrentBrightness);
            Assert.Equal(10.0, vm.CurrentPercent);

            // Setting to 2% (20 < 50) must trigger the safeguard callback!
            vm.SliderPercent = 2;
            vm.ApplySliderBrightness();

            Assert.True(safeguardTriggered);
            Assert.Equal(20, requestedRaw);
            Assert.Equal(2.0, requestedPercent);
            // Notice: hardware must NOT be changed yet!
            Assert.Equal(100, vm.CurrentBrightness);

            // If user confirms:
            vm.ApplyRawBrightness(requestedRaw);
            Assert.Equal(20, vm.CurrentBrightness);
            Assert.Equal(2.0, vm.CurrentPercent);

            // Test cancellation reverting slider
            vm.SliderPercent = 1;
            vm.RevertSliderToSafe();
            Assert.True(vm.SliderPercent >= 5.0);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void OnScreenKeyboardViewModel_LayoutLayersAndKeyHandling()
    {
        var osk = new OnScreenKeyboardViewModel();

        // Default state: lowercase QWERTY
        Assert.NotEmpty(osk.Row1Keys);
        Assert.Equal("q", osk.Row1Keys[0].DisplayText);
        Assert.False(osk.IsShiftActive);
        Assert.False(osk.IsSymbolsMode);

        // Test Shift
        var shiftKey = osk.Row3Keys[0];
        Assert.Equal("SHIFT", shiftKey.OutputValue);
        osk.HandleKeyPress(shiftKey);

        Assert.True(osk.IsShiftActive);
        Assert.Equal("Q", osk.Row1Keys[0].DisplayText);

        // Test Symbols Mode (?123)
        var symKey = osk.Row4Keys[0];
        Assert.Equal("MODE_SYMBOLS", symKey.OutputValue);
        osk.HandleKeyPress(symKey);

        Assert.True(osk.IsSymbolsMode);
        Assert.False(osk.IsExtendedSymbolsMode);
        Assert.Equal("1", osk.Row1Keys[0].DisplayText);

        // Test Extended Symbols Mode (=\<)
        var extKey = osk.Row3Keys[0];
        Assert.Equal("MODE_EXTENDED", extKey.OutputValue);
        osk.HandleKeyPress(extKey);

        Assert.True(osk.IsExtendedSymbolsMode);
        Assert.Equal("~", osk.Row1Keys[0].DisplayText);

        // Switch back to letters
        var abcKey = osk.Row4Keys[0];
        Assert.Equal("MODE_LETTERS", abcKey.OutputValue);
        osk.HandleKeyPress(abcKey);

        Assert.False(osk.IsSymbolsMode);
        Assert.False(osk.IsExtendedSymbolsMode);
        Assert.Equal("q", osk.Row1Keys[0].DisplayText);
    }

    [Fact]
    public void OnScreenKeyboardViewModel_InsertsTextIntoTargetTextBox()
    {
        var osk = new OnScreenKeyboardViewModel();
        var textBox = new TextBox { Text = "" };
        osk.SetTarget(textBox);

        // Type "cat"
        osk.HandleKeyPress(new KeyboardKeyViewModel("c", "c"));
        osk.HandleKeyPress(new KeyboardKeyViewModel("a", "a"));
        osk.HandleKeyPress(new KeyboardKeyViewModel("t", "t"));

        Assert.Equal("cat", textBox.Text);
        Assert.Equal(3, textBox.CaretIndex);

        // Quick networking keys: "@", "/", ".", ":"
        osk.HandleKeyPress(new KeyboardKeyViewModel("@", "@"));
        osk.HandleKeyPress(new KeyboardKeyViewModel(".", "."));
        osk.HandleKeyPress(new KeyboardKeyViewModel(":", ":"));
        osk.HandleKeyPress(new KeyboardKeyViewModel("/", "/"));

        Assert.Equal("cat@.:/", textBox.Text);

        // Backspace
        osk.HandleKeyPress(new KeyboardKeyViewModel("⌫", "BACKSPACE"));
        Assert.Equal("cat@.:", textBox.Text);

        // Space
        osk.HandleKeyPress(new KeyboardKeyViewModel("Space", "SPACE"));
        Assert.Equal("cat@.: ", textBox.Text);
    }
}
