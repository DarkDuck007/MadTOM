using System;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MADTOM.Console.ViewModels;

public sealed partial class KeyboardKeyViewModel : ObservableObject
{
    public string DisplayText { get; }
    public string OutputValue { get; }
    public double FlexWidth { get; }
    public bool IsSpecial { get; }
    public bool IsActive { get; }

    public double KeyWidth => FlexWidth * 56.0;

    public KeyboardKeyViewModel(string displayText, string outputValue, double flexWidth = 1.0, bool isSpecial = false, bool isActive = false)
    {
        DisplayText = displayText;
        OutputValue = outputValue;
        FlexWidth = flexWidth;
        IsSpecial = isSpecial;
        IsActive = isActive;
    }
}

public sealed partial class OnScreenKeyboardViewModel : ObservableObject
{
    private TextBox? _targetTextBox;
    private TopLevel? _topLevel;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private bool _isShiftActive;

    [ObservableProperty]
    private bool _isCapsLock;

    [ObservableProperty]
    private bool _isSymbolsMode;

    [ObservableProperty]
    private bool _isExtendedSymbolsMode;

    [ObservableProperty]
    private bool _isAutoPopupEnabled = true;

    public ObservableCollection<KeyboardKeyViewModel> Row1Keys { get; } = new();
    public ObservableCollection<KeyboardKeyViewModel> Row2Keys { get; } = new();
    public ObservableCollection<KeyboardKeyViewModel> Row3Keys { get; } = new();
    public ObservableCollection<KeyboardKeyViewModel> Row4Keys { get; } = new();

    public OnScreenKeyboardViewModel()
    {
        RefreshKeyLayout();
    }

    public void AttachToTopLevel(TopLevel topLevel)
    {
        _topLevel = topLevel;

        topLevel.AddHandler(InputElement.GotFocusEvent, (sender, e) =>
        {
            var tb = e.Source as TextBox ?? (e.Source as Visual)?.FindAncestorOfType<TextBox>();
            if (tb != null)
            {
                _targetTextBox = tb;
                if (IsAutoPopupEnabled && !IsVisible)
                {
                    IsVisible = true;
                }
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        topLevel.AddHandler(InputElement.PointerPressedEvent, (sender, e) =>
        {
            var tb = e.Source as TextBox ?? (e.Source as Visual)?.FindAncestorOfType<TextBox>();
            if (tb != null)
            {
                _targetTextBox = tb;
                if (IsAutoPopupEnabled && !IsVisible)
                {
                    IsVisible = true;
                }
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    public void SetTarget(TextBox? tb)
    {
        _targetTextBox = tb;
    }

    partial void OnIsShiftActiveChanged(bool value) => RefreshKeyLayout();
    partial void OnIsCapsLockChanged(bool value) => RefreshKeyLayout();
    partial void OnIsSymbolsModeChanged(bool value) => RefreshKeyLayout();
    partial void OnIsExtendedSymbolsModeChanged(bool value) => RefreshKeyLayout();

    public void RefreshKeyLayout()
    {
        Row1Keys.Clear();
        Row2Keys.Clear();
        Row3Keys.Clear();
        Row4Keys.Clear();

        if (IsSymbolsMode)
        {
            if (IsExtendedSymbolsMode)
            {
                // Extended Symbols
                AddKeys(Row1Keys, new[] { "~", "`", "|", "\\", "^", "{", "}", "[", "]", "§" });
                AddKeys(Row2Keys, new[] { "€", "£", "¥", "°", "=", "<", ">", "¡", "¿", "«" });
                // Row 3
                Row3Keys.Add(new KeyboardKeyViewModel("?123", "MODE_SYMBOLS", flexWidth: 1.5, isSpecial: true));
                AddKeys(Row3Keys, new[] { "»", "©", "®", "™", "+", "-", "*", "/" });
                Row3Keys.Add(new KeyboardKeyViewModel("⌫", "BACKSPACE", flexWidth: 1.5, isSpecial: true));
            }
            else
            {
                // Basic Numbers & Symbols
                AddKeys(Row1Keys, new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" });
                AddKeys(Row2Keys, new[] { "-", "/", ":", ";", "(", ")", "$", "&", "@", "\"" });
                // Row 3
                Row3Keys.Add(new KeyboardKeyViewModel("=\\<", "MODE_EXTENDED", flexWidth: 1.5, isSpecial: true));
                AddKeys(Row3Keys, new[] { ".", ",", "?", "!", "'", "#", "%", "*" });
                Row3Keys.Add(new KeyboardKeyViewModel("⌫", "BACKSPACE", flexWidth: 1.5, isSpecial: true));
            }

            // Bottom Row for Symbols
            Row4Keys.Add(new KeyboardKeyViewModel("ABC", "MODE_LETTERS", flexWidth: 1.5, isSpecial: true));
            Row4Keys.Add(new KeyboardKeyViewModel("_", "_", flexWidth: 1.0));
            Row4Keys.Add(new KeyboardKeyViewModel("+", "+", flexWidth: 1.0));
            Row4Keys.Add(new KeyboardKeyViewModel("Space", "SPACE", flexWidth: 3.5));
            Row4Keys.Add(new KeyboardKeyViewModel("=", "=", flexWidth: 1.0));
            Row4Keys.Add(new KeyboardKeyViewModel("Enter", "ENTER", flexWidth: 1.5, isSpecial: true));
            Row4Keys.Add(new KeyboardKeyViewModel("⌨ ⌵", "HIDE", flexWidth: 1.2, isSpecial: true));
        }
        else
        {
            // Standard US QWERTY Letters
            bool upper = IsShiftActive || IsCapsLock;

            string[] r1 = upper 
                ? new[] { "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P" }
                : new[] { "q", "w", "e", "r", "t", "y", "u", "i", "o", "p" };

            string[] r2 = upper
                ? new[] { "A", "S", "D", "F", "G", "H", "J", "K", "L" }
                : new[] { "a", "s", "d", "f", "g", "h", "j", "k", "l" };

            string[] r3 = upper
                ? new[] { "Z", "X", "C", "V", "B", "N", "M" }
                : new[] { "z", "x", "c", "v", "b", "n", "m" };

            AddKeys(Row1Keys, r1);
            AddKeys(Row2Keys, r2);

            // Shift button with visual indicator
            string shiftLabel = IsCapsLock ? "⇪ LOCK" : (IsShiftActive ? "⬆ ON" : "⇧");
            Row3Keys.Add(new KeyboardKeyViewModel(shiftLabel, "SHIFT", flexWidth: 1.5, isSpecial: true, isActive: IsShiftActive || IsCapsLock));
            AddKeys(Row3Keys, r3);
            Row3Keys.Add(new KeyboardKeyViewModel("⌫", "BACKSPACE", flexWidth: 1.5, isSpecial: true));

            // Bottom Row (Direct access to @, /, ., : for IP/MAC/URLs/Domains)
            Row4Keys.Add(new KeyboardKeyViewModel("?123", "MODE_SYMBOLS", flexWidth: 1.3, isSpecial: true));
            Row4Keys.Add(new KeyboardKeyViewModel("@", "@", flexWidth: 0.9));
            Row4Keys.Add(new KeyboardKeyViewModel("/", "/", flexWidth: 0.9));
            Row4Keys.Add(new KeyboardKeyViewModel("Space", "SPACE", flexWidth: 3.5));
            Row4Keys.Add(new KeyboardKeyViewModel(".", ".", flexWidth: 0.9));
            Row4Keys.Add(new KeyboardKeyViewModel(":", ":", flexWidth: 0.9));
            Row4Keys.Add(new KeyboardKeyViewModel("Enter", "ENTER", flexWidth: 1.4, isSpecial: true));
            Row4Keys.Add(new KeyboardKeyViewModel("⌨ ⌵", "HIDE", flexWidth: 1.1, isSpecial: true));
        }
    }

    private static void AddKeys(ObservableCollection<KeyboardKeyViewModel> collection, string[] keys)
    {
        foreach (var k in keys)
        {
            collection.Add(new KeyboardKeyViewModel(k, k));
        }
    }

    [RelayCommand]
    public void HandleKeyPress(KeyboardKeyViewModel key)
    {
        if (key == null) return;

        if (_targetTextBox == null && _topLevel != null)
        {
            var focused = _topLevel.FocusManager?.GetFocusedElement();
            _targetTextBox = focused as TextBox ?? (focused as Visual)?.FindAncestorOfType<TextBox>();
        }

        switch (key.OutputValue)
        {
            case "SHIFT":
                if (IsCapsLock)
                {
                    IsCapsLock = false;
                    IsShiftActive = false;
                }
                else if (IsShiftActive)
                {
                    IsCapsLock = true;
                }
                else
                {
                    IsShiftActive = true;
                }
                break;

            case "MODE_SYMBOLS":
                IsSymbolsMode = true;
                IsExtendedSymbolsMode = false;
                break;

            case "MODE_EXTENDED":
                IsExtendedSymbolsMode = true;
                break;

            case "MODE_LETTERS":
                IsSymbolsMode = false;
                IsExtendedSymbolsMode = false;
                IsShiftActive = false;
                IsCapsLock = false;
                break;

            case "HIDE":
                IsVisible = false;
                break;

            case "BACKSPACE":
                HandleBackspace();
                break;

            case "SPACE":
                InsertString(" ");
                break;

            case "ENTER":
                HandleEnter();
                break;

            default:
                InsertString(key.OutputValue);
                if (IsShiftActive && !IsCapsLock)
                {
                    IsShiftActive = false;
                }
                break;
        }
    }

    private void InsertString(string text)
    {
        if (_targetTextBox == null) return;

        string current = _targetTextBox.Text ?? string.Empty;
        int start = Math.Min(_targetTextBox.SelectionStart, _targetTextBox.SelectionEnd);
        int end = Math.Max(_targetTextBox.SelectionStart, _targetTextBox.SelectionEnd);

        if (start != end)
        {
            current = current.Remove(start, end - start);
            current = current.Insert(start, text);
            _targetTextBox.Text = current;
            _targetTextBox.CaretIndex = start + text.Length;
        }
        else
        {
            int caret = Math.Clamp(_targetTextBox.CaretIndex, 0, current.Length);
            current = current.Insert(caret, text);
            _targetTextBox.Text = current;
            _targetTextBox.CaretIndex = caret + text.Length;
        }
    }

    private void HandleBackspace()
    {
        if (_targetTextBox == null) return;

        string current = _targetTextBox.Text ?? string.Empty;
        int start = Math.Min(_targetTextBox.SelectionStart, _targetTextBox.SelectionEnd);
        int end = Math.Max(_targetTextBox.SelectionStart, _targetTextBox.SelectionEnd);

        if (start != end)
        {
            current = current.Remove(start, end - start);
            _targetTextBox.Text = current;
            _targetTextBox.CaretIndex = start;
        }
        else
        {
            int caret = Math.Clamp(_targetTextBox.CaretIndex, 0, current.Length);
            if (caret > 0)
            {
                current = current.Remove(caret - 1, 1);
                _targetTextBox.Text = current;
                _targetTextBox.CaretIndex = caret - 1;
            }
        }
    }

    private void HandleEnter()
    {
        if (_targetTextBox == null) return;

        if (_targetTextBox.AcceptsReturn)
        {
            InsertString(Environment.NewLine);
        }
        else
        {
            // For single-line textboxes, pressing Enter finishes typing or hides keyboard
            IsVisible = false;
        }
    }

    [RelayCommand]
    public void ToggleVisibility()
    {
        IsVisible = !IsVisible;
    }
}
