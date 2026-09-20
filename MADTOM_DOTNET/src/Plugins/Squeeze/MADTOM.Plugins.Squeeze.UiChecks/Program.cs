using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SQUEEZE.Services;
using SQUEEZE.ViewModels;
using SQUEEZE.Views;

AppBuilder.Configure<CheckApplication>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
SqueezeThemeService.InitializeStandaloneDefaults();
var presets = new PresetService();
var vm = new MainViewModel(presets, new MockTranscoderBackendService());
var view = new MainView { DataContext = vm };
var window = new Window { Content = view, Width = 1280, Height = 820 };
window.Show();
void Layout() { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
T Find<T>(string name) where T : Control => view.FindControl<T>(name) ?? throw new Exception(name);
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
void Shot(string name) { if (args.Length > 0) { Directory.CreateDirectory(args[0]); using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(args[0], name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); } }

foreach (var (width, height) in new[] { (1280, 820), (800, 600), (390, 844), (360, 640), (320, 568), (740, 360) })
{
    window.Width = width; window.Height = height; Layout();
    var editor = Find<Border>("EditorPanel");
    Check(editor.IsVisible, "Editor should be the initial mobile pane");
    Check(editor.Bounds.Width <= width && editor.Bounds.Width >= (width < 760 ? width - 40 : 300), $"Editor width at {width}: {editor.Bounds}");
    if (width < 760)
    {
        var navigation = Find<Grid>("MobileNavigation").Children.OfType<Button>().ToArray();
        Check(!navigation[0].Bounds.Intersects(navigation[1].Bounds), "Mobile navigation buttons overlap");
        Check(navigation.All(b => b.Bounds.Height >= 40), "Touch targets are too small");
        var scroll = Find<ScrollViewer>("EditorScroll");
        Check(scroll.Viewport.Height > 60, "Editor is not scrollable on short screens");
        Check(scroll.Extent.Height >= scroll.Viewport.Height, "Editor extent lost its content");
    }
    Find<Button>("PresetsButton").Focus();
    Shot($"editor-{width}x{height}");
    vm.ToggleMegaMenu(); Layout();
    var dialog = Find<Grid>("PresetDialog");
    Check(dialog.Bounds.Width <= width && dialog.Bounds.Height <= height, $"Catalog escaped {width}x{height}: {dialog.Bounds}");
    Check(Find<TextBox>("PresetSearch").IsFocused, "Opening presets should focus search");
    Check(!Find<Grid>("Workspace").IsEffectivelyEnabled, "Modal should block workspace keyboard input");
    Check(Find<ScrollViewer>("PresetScroll").Viewport.Height > 50, "Preset list has no useful scroll viewport");
    Shot($"presets-{width}x{height}");
    window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Layout();
    Check(!vm.IsMegaMenuOpen, "Escape did not close presets");
    Check(Find<Button>("PresetsButton").IsFocused, "Dialog did not restore keyboard focus");
    vm.OpenSettings(); Layout();
    Check(Find<SettingsDialog>("ConnectionDialog").Bounds.Height <= height, "Settings exceeds viewport");
    Shot($"settings-{width}x{height}");
    window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Layout();
    vm.OpenAddPresetModal(); Layout(); Shot($"save-{width}x{height}");
    window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Layout();
    vm.OpenRigInfo(); Layout(); Shot($"hardware-{width}x{height}");
    window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Layout();
    Console.WriteLine($"PASS layout {width}x{height}");
}

window.Width = 390; window.Height = 844; Layout();
Click(Find<Grid>("MobileNavigation").Children.OfType<Button>().Last()); Layout();
Check(Find<Grid>("QueuePanel").IsVisible && !Find<Border>("EditorPanel").IsVisible, "Queue switch failed");
Shot("mobile-queue");
Click(Find<Grid>("MobileNavigation").Children.OfType<Button>().First()); Layout();
var tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
for (int i = 0; i < tabs.ItemCount; i++)
{
    tabs.SelectedIndex = i; Layout(); Shot($"mobile-tab-{i}");
    foreach (var grid in Find<Border>("EditorPanel").GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("form-row") && g.IsEffectivelyVisible))
        Check(grid.ColumnDefinitions.Count == 1, "Newly selected parameter tab did not adapt to mobile");
}
window.Width = 1280; window.Height = 820; Layout();
Check(Find<Grid>("QueuePanel").IsVisible && Find<Border>("EditorPanel").IsVisible, "Desktop panels not restored");
vm.SidebarColumnWidth = new GridLength(410); vm.ToggleJobQueueCollapse(); Layout();
window.Width = 390; Layout(); window.Width = 1280; Layout();
Check(vm.IsJobQueueCollapsed, "Desktop collapse state lost on rotation");
vm.ToggleJobQueueCollapse(); Check(vm.SidebarColumnWidth.Value == 410, "Sidebar width lost on rotation");
vm.ToggleMegaMenu(); Layout();
var handle = Find<Border>("ResizeBottomRight");
var start = handle.TranslatePoint(new Point(4, 4), window)!.Value;
var oldWidth = Find<Grid>("PresetDialog").Bounds.Width;
window.MouseDown(start, MouseButton.Left); window.MouseMove(start + new Vector(70, 45)); window.MouseUp(start + new Vector(70, 45), MouseButton.Left); Layout();
Check(Find<Grid>("PresetDialog").Bounds.Width > oldWidth, "Resize drag did not enlarge dialog");
var resizedWidth = Find<Grid>("PresetDialog").Bounds.Width;
window.Width = 800; window.Height = 400; Layout();
Check(Find<Grid>("PresetDialog").Bounds.Width <= 800 && Find<Grid>("PresetDialog").Bounds.Height <= 400, "Dialog did not shrink to host viewport");
window.Width = 1280; window.Height = 820; Layout();
Check(Find<Grid>("PresetDialog").Bounds.Width == resizedWidth, "Preferred dialog size lost after host resize");
vm.CloseMegaMenu(); vm.ToggleMegaMenu(); Layout();
Check(Find<Grid>("PresetDialog").Bounds.Width > oldWidth, "Dialog size not remembered");
vm.CloseMegaMenu(); Layout();
foreach (var (name, open, close) in new (string, Action, Action)[]
{
    ("HardwareDialog", vm.OpenRigInfo, vm.CloseRigInfo),
    ("SavePresetDialog", vm.OpenAddPresetModal, vm.CloseAddPresetModal),
    ("SettingsWindow", vm.OpenSettings, vm.CloseSettings)
})
{
    open(); Layout();
    var dialog = Find<Grid>(name);
    var grip = dialog.GetVisualDescendants().OfType<Border>().Single(b => b.Name == name + "ResizeBottomRight");
    var point = grip.TranslatePoint(new Point(3, 3), window)!.Value;
    var before = dialog.Bounds.Size;
    window.MouseDown(point, MouseButton.Left);
    window.MouseMove(point + new Vector(40, 30));
    window.MouseUp(point + new Vector(40, 30), MouseButton.Left); Layout();
    Check(dialog.Bounds.Width > before.Width && dialog.Bounds.Height > before.Height, name + " did not resize");
    Shot(name + "-resized");
    window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Layout();
    Check(!vm.IsAnyDialogOpen, name + " did not close with Escape");
}
Check(ReferenceEquals(Find<Button>("SourcePicker").Command, vm.BrowseFilesCommand), "Source bar must open file picker");
Check(vm.PresetGroups.Count(g => g.Presets.Count > 0) > 1, "Default catalog must expose separate groups");
var firstGroup = vm.PresetGroups.First(g => g.Presets.Count > 0);
firstGroup.IsExpanded = false;
vm.SearchQuery = "1080p";
Check(firstGroup.IsExpanded, "Search must reveal matching group entries");
vm.ClearPresetFiltersCommand.Execute(null);
Check(!firstGroup.IsExpanded, "Clearing search should restore collapsed groups");
vm.OpenAddPresetModal();
vm.AddPresetModal!.PresetName = "Grouped user profile";
vm.AddPresetModal.SelectedCategory = "Web";
vm.AddPresetModal.Save();
var groupedKey = vm.ActivePreset!.Key;
Check(vm.PresetGroups.Single(g => g.Name == "Web").Presets.Any(p => p.Key == groupedKey), "New preset not saved to chosen group");
presets.LoadServerPresets(new[] { new SQUEEZE.Models.TranscodePreset { Key = "server-profile", Title = "Server profile", Category = "General" } });
Layout();
Check(presets.GetPresetByKey(groupedKey)?.Category == "Web", "Server refresh lost grouped user preset");
// Restore the built-in catalog for the remaining search checks.
presets.LoadServerPresets(new PresetService().GetAllPresets()); Layout();
vm.ToggleMegaMenu(); Layout();
vm.SearchQuery = "  1080p libx264 "; Check(vm.FilteredPresets.Count > 0 && vm.FilteredPresets.All(p => p.Codec == "libx264"), "Multi-term search failed");
vm.SearchQuery = "no-such-preset-981"; Check(vm.FilteredPresets.Count == 0, "No-result search failed");
vm.ClearPresetFiltersCommand.Execute(null);
Check(vm.FilteredPresets.Count == presets.GetAllPresets().Count, "Reset filters failed");
vm.CloseMegaMenu(); vm.OpenAddPresetModal(); vm.AddPresetModal!.PresetName = "UI check custom"; vm.AddPresetModal.Save();
vm.SelectedPresetCategory = "Custom";
Check(vm.FilteredPresets.Any(p => p.Title == "UI check custom"), "Saved custom preset not discoverable");
vm.SelectPreset(vm.FilteredPresets.First()); Check(!vm.IsCustomPreset, "Preset selection did not load settings");
vm.SetDefaultPreset(); Check(presets.DefaultPresetKey == vm.ActivePreset!.Key, "Default preset action failed");
vm.CloseMegaMenu(); Layout();
var copy = Find<Button>("CopyPreviewButton");
Click(copy); Layout();
Check(window.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult() == vm.TranscodeParams.RawCliInput, "Copy preview failed");
// Runtime resource replacement must flow through existing views without rebuilding them.
Application.Current!.Resources["CardBgBrush"] = new SolidColorBrush(Colors.White);
Application.Current.Resources["TextPrimaryBrush"] = new SolidColorBrush(Colors.Black);
Layout();
Check(((ISolidColorBrush)Find<Border>("EditorPanel").Background!).Color == Colors.White, "Theme resource update did not reach editor");
Console.WriteLine("PASS navigation, resizing, filters, custom/default presets, dynamic theme updates");
window.Close();

public class CheckApplication : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }
}
