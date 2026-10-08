using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace MADTOM.PluginContracts.Controls;

/// <summary>
/// Universal modal dialog control providing:
/// - Full-screen dimmed backdrop scrim (#B3000000)
/// - Centered card container with theme-aware styling, rounded corners, and shadow
/// - Title, subtitle, icon glyph badge, and close button
/// - Universal resizing with both mouse borders and dedicated touchscreen touch grip
/// - Escape key dismissal and optional backdrop click dismissal
/// </summary>
public class UniversalModalControl : ContentControl
{
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<UniversalModalControl, bool>(nameof(IsOpen), defaultValue: false);

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<UniversalModalControl, string>(nameof(Title), defaultValue: string.Empty);

    public static readonly StyledProperty<string> SubtitleProperty =
        AvaloniaProperty.Register<UniversalModalControl, string>(nameof(Subtitle), defaultValue: string.Empty);

    public static readonly StyledProperty<object?> IconProperty =
        AvaloniaProperty.Register<UniversalModalControl, object?>(nameof(Icon), defaultValue: "⚙");

    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<UniversalModalControl, ICommand?>(nameof(CloseCommand));

    public static readonly StyledProperty<bool> DismissOnBackdropClickProperty =
        AvaloniaProperty.Register<UniversalModalControl, bool>(nameof(DismissOnBackdropClick), defaultValue: false);

    public static readonly StyledProperty<double> DialogWidthProperty =
        AvaloniaProperty.Register<UniversalModalControl, double>(nameof(DialogWidth), defaultValue: 720.0);

    public static readonly StyledProperty<double> DialogHeightProperty =
        AvaloniaProperty.Register<UniversalModalControl, double>(nameof(DialogHeight), defaultValue: 620.0);

    public static readonly StyledProperty<double> DialogMinWidthProperty =
        AvaloniaProperty.Register<UniversalModalControl, double>(nameof(DialogMinWidth), defaultValue: 400.0);

    public static readonly StyledProperty<double> DialogMinHeightProperty =
        AvaloniaProperty.Register<UniversalModalControl, double>(nameof(DialogMinHeight), defaultValue: 300.0);

    public static readonly StyledProperty<double> DialogMaxWidthProperty =
        AvaloniaProperty.Register<UniversalModalControl, double>(nameof(DialogMaxWidth), defaultValue: double.PositiveInfinity);

    public static readonly StyledProperty<double> DialogMaxHeightProperty =
        AvaloniaProperty.Register<UniversalModalControl, double>(nameof(DialogMaxHeight), defaultValue: double.PositiveInfinity);

    public static readonly StyledProperty<bool> ShowCloseButtonProperty =
        AvaloniaProperty.Register<UniversalModalControl, bool>(nameof(ShowCloseButton), defaultValue: true);

    public static readonly StyledProperty<bool> ShowTouchGripProperty =
        AvaloniaProperty.Register<UniversalModalControl, bool>(nameof(ShowTouchGrip), defaultValue: true);

    public static readonly StyledProperty<object?> HeaderExtraContentProperty =
        AvaloniaProperty.Register<UniversalModalControl, object?>(nameof(HeaderExtraContent));

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public object? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    public bool DismissOnBackdropClick
    {
        get => GetValue(DismissOnBackdropClickProperty);
        set => SetValue(DismissOnBackdropClickProperty, value);
    }

    public double DialogWidth
    {
        get => GetValue(DialogWidthProperty);
        set => SetValue(DialogWidthProperty, value);
    }

    public double DialogHeight
    {
        get => GetValue(DialogHeightProperty);
        set => SetValue(DialogHeightProperty, value);
    }

    public double DialogMinWidth
    {
        get => GetValue(DialogMinWidthProperty);
        set => SetValue(DialogMinWidthProperty, value);
    }

    public double DialogMinHeight
    {
        get => GetValue(DialogMinHeightProperty);
        set => SetValue(DialogMinHeightProperty, value);
    }

    public double DialogMaxWidth
    {
        get => GetValue(DialogMaxWidthProperty);
        set => SetValue(DialogMaxWidthProperty, value);
    }

    public double DialogMaxHeight
    {
        get => GetValue(DialogMaxHeightProperty);
        set => SetValue(DialogMaxHeightProperty, value);
    }

    public bool ShowCloseButton
    {
        get => GetValue(ShowCloseButtonProperty);
        set => SetValue(ShowCloseButtonProperty, value);
    }

    public bool ShowTouchGrip
    {
        get => GetValue(ShowTouchGripProperty);
        set => SetValue(ShowTouchGripProperty, value);
    }

    public object? HeaderExtraContent
    {
        get => GetValue(HeaderExtraContentProperty);
        set => SetValue(HeaderExtraContentProperty, value);
    }

    private Border? _dialogContainer;
    private Grid? _scrimGrid;

    public UniversalModalControl()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        IsVisible = IsOpen;

        Template = new Avalonia.Controls.Templates.FuncControlTemplate((control, scope) =>
        {
            var parent = (UniversalModalControl)control;
            return parent.CreateModalTemplate();
        });

        AddHandler(KeyDownEvent, (s, e) =>
        {
            if (e.Key == Key.Escape && IsOpen)
            {
                RequestClose();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsOpenProperty)
        {
            IsVisible = change.GetNewValue<bool>();
        }
        else if (change.Property == DialogWidthProperty && _dialogContainer != null)
        {
            _dialogContainer.Width = change.GetNewValue<double>();
        }
        else if (change.Property == DialogHeightProperty && _dialogContainer != null)
        {
            _dialogContainer.Height = change.GetNewValue<double>();
        }
    }

    public void RequestClose()
    {
        if (CloseCommand != null && CloseCommand.CanExecute(null))
        {
            CloseCommand.Execute(null);
        }
        else
        {
            IsOpen = false;
        }
    }

    private Control CreateModalTemplate()
    {
        // Full-screen dimmed backdrop scrim (#B3000000)
        _scrimGrid = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x00, 0x00, 0x00)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        _scrimGrid.PointerPressed += (s, e) =>
        {
            if (DismissOnBackdropClick && e.Source == _scrimGrid)
            {
                RequestClose();
                e.Handled = true;
            }
        };

        // Dialog Container Card (Centered, rounded, theme-aware)
        _dialogContainer = new Border
        {
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            BoxShadow = BoxShadows.Parse("0 14 36 #C0000000")
        };

        _dialogContainer.Bind(Border.BackgroundProperty, this.GetResourceObservable("CardBgBrush"));
        _dialogContainer.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BorderBrush"));

        _dialogContainer.Bind(Border.WidthProperty, this.GetObservable(DialogWidthProperty));
        _dialogContainer.Bind(Border.HeightProperty, this.GetObservable(DialogHeightProperty));
        _dialogContainer.Bind(Border.MinWidthProperty, this.GetObservable(DialogMinWidthProperty));
        _dialogContainer.Bind(Border.MinHeightProperty, this.GetObservable(DialogMinHeightProperty));
        _dialogContainer.Bind(Border.MaxWidthProperty, this.GetObservable(DialogMaxWidthProperty));
        _dialogContainer.Bind(Border.MaxHeightProperty, this.GetObservable(DialogMaxHeightProperty));

        var containerPanel = new Panel();

        // Main layout inside card (Header, Content)
        var layoutGrid = new Grid
        {
            Margin = new Thickness(24),
            RowDefinitions = new RowDefinitions("Auto,*")
        };

        // Header Grid
        var headerGrid = new Grid
        {
            Margin = new Thickness(0, 0, 0, 16),
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };

        var titleStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Icon Badge
        var iconBadge = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 5),
            VerticalAlignment = VerticalAlignment.Center
        };
        iconBadge.Bind(Border.BackgroundProperty, this.GetResourceObservable("AccentBrush"));

        var iconText = new TextBlock
        {
            FontSize = 14,
            Foreground = Brushes.White,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        iconText.Bind(TextBlock.TextProperty, this.GetObservable(IconProperty));
        iconBadge.Child = iconText;
        titleStack.Children.Add(iconBadge);

        // Titles
        var textStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var titleBlock = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            LetterSpacing = 0.5
        };
        titleBlock.Bind(TextBlock.TextProperty, this.GetObservable(TitleProperty));
        titleBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("TextPrimaryBrush"));
        textStack.Children.Add(titleBlock);

        var subtitleBlock = new TextBlock
        {
            FontSize = 11
        };
        subtitleBlock.Bind(TextBlock.TextProperty, this.GetObservable(SubtitleProperty));
        subtitleBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("TextSecondaryBrush"));
        textStack.Children.Add(subtitleBlock);
        titleStack.Children.Add(textStack);

        headerGrid.Children.Add(titleStack);
        Grid.SetColumn(titleStack, 0);

        // Header Actions (Right)
        var headerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        var extraPresenter = new ContentPresenter();
        extraPresenter.Bind(ContentPresenter.ContentProperty, this.GetObservable(HeaderExtraContentProperty));
        headerActions.Children.Add(extraPresenter);

        var closeBtn = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 4),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = new TextBlock
            {
                Text = "✕",
                FontSize = 15,
                FontWeight = FontWeight.Bold
            }
        };
        closeBtn.Bind(Button.IsVisibleProperty, this.GetObservable(ShowCloseButtonProperty));
        closeBtn.Click += (_, _) => RequestClose();
        headerActions.Children.Add(closeBtn);

        headerGrid.Children.Add(headerActions);
        Grid.SetColumn(headerActions, 1);

        layoutGrid.Children.Add(headerGrid);
        Grid.SetRow(headerGrid, 0);

        // Content Presenter for Dialog Body
        var bodyPresenter = new ContentPresenter
        {
            Name = "PART_ContentPresenter"
        };
        bodyPresenter.Bind(ContentPresenter.ContentProperty, this.GetObservable(ContentProperty));
        bodyPresenter.Bind(ContentPresenter.ContentTemplateProperty, this.GetObservable(ContentTemplateProperty));
        layoutGrid.Children.Add(bodyPresenter);
        Grid.SetRow(bodyPresenter, 1);

        containerPanel.Children.Add(layoutGrid);

        // 8 Symmetric Resize Handles (Mouse & Touch)
        var resizeLeft = new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch, Width = 10, Margin = new Thickness(0, 14), Cursor = new Cursor(StandardCursorType.SizeWestEast), Background = Brushes.Transparent };
        var resizeRight = new Border { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Stretch, Width = 10, Margin = new Thickness(0, 14), Cursor = new Cursor(StandardCursorType.SizeWestEast), Background = Brushes.Transparent };
        var resizeTop = new Border { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top, Height = 10, Margin = new Thickness(14, 0), Cursor = new Cursor(StandardCursorType.SizeNorthSouth), Background = Brushes.Transparent };
        var resizeBottom = new Border { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Bottom, Height = 10, Margin = new Thickness(14, 0), Cursor = new Cursor(StandardCursorType.SizeNorthSouth), Background = Brushes.Transparent };

        var resizeTopLeft = new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Width = 16, Height = 16, Cursor = new Cursor(StandardCursorType.TopLeftCorner), Background = Brushes.Transparent };
        var resizeTopRight = new Border { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Width = 16, Height = 16, Cursor = new Cursor(StandardCursorType.TopRightCorner), Background = Brushes.Transparent };
        var resizeBottomLeft = new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Width = 16, Height = 16, Cursor = new Cursor(StandardCursorType.BottomLeftCorner), Background = Brushes.Transparent };
        var resizeBottomRight = new Border { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Width = 16, Height = 16, Cursor = new Cursor(StandardCursorType.BottomRightCorner), Background = Brushes.Transparent };

        containerPanel.Children.Add(resizeLeft);
        containerPanel.Children.Add(resizeRight);
        containerPanel.Children.Add(resizeTop);
        containerPanel.Children.Add(resizeBottom);
        containerPanel.Children.Add(resizeTopLeft);
        containerPanel.Children.Add(resizeTopRight);
        containerPanel.Children.Add(resizeBottomLeft);
        containerPanel.Children.Add(resizeBottomRight);

        // Dedicated Touchscreen Resize Grip Handle in bottom-right corner
        var touchGrip = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Width = 32,
            Height = 32,
            Cursor = new Cursor(StandardCursorType.BottomRightCorner),
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 0, 2, 2),
            Child = new TextBlock
            {
                Text = "◢",
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 4, 3),
                Opacity = 0.55
            }
        };
        touchGrip.Bind(Border.IsVisibleProperty, this.GetObservable(ShowTouchGripProperty));
        containerPanel.Children.Add(touchGrip);

        // Attach Universal Resizer
        CenteredDialogResizer.Attach(
            _dialogContainer,
            resizeLeft, resizeRight, resizeTop, resizeBottom,
            resizeTopLeft, resizeTopRight, resizeBottomLeft, resizeBottomRight,
            touchGrip: touchGrip,
            minWidth: DialogMinWidth, minHeight: DialogMinHeight,
            maxWidth: DialogMaxWidth, maxHeight: DialogMaxHeight,
            symmetric: true);

        _dialogContainer.Child = containerPanel;
        _scrimGrid.Children.Add(_dialogContainer);

        return _scrimGrid;
    }
}
