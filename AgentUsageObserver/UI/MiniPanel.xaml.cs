using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using AgentUsageObserver.Models;
using AgentUsageObserver.Services.Localization;

namespace AgentUsageObserver.UI;

/// <summary>
/// Mini panel emergente (clic simple en el icono). Muestra una seccion por agente
/// con sus ventanas de uso, porcentaje y tiempo hasta el reset.
/// </summary>
public partial class MiniPanel : Window
{
    private readonly Action _openSettings;
    private readonly Action<string> _refreshProvider;
    private readonly Dictionary<string, RotateTransform> _refreshRotates = new();
    private readonly HashSet<string> _spinningProviders = new();

    private static readonly Color Green = Color.FromRgb(46, 160, 67);
    private static readonly Color Yellow = Color.FromRgb(210, 153, 34);
    private static readonly Color Red = Color.FromRgb(218, 54, 51);
    private static readonly Color Gray = Color.FromRgb(110, 118, 129);

    public MiniPanel(Action openSettings, Action<string> refreshProvider)
    {
        _openSettings = openSettings;
        _refreshProvider = refreshProvider;
        InitializeComponent();
    }

    /// <summary>Refresca el contenido con un unico snapshot.</summary>
    public void Update(UsageSnapshot snapshot) => Update(new[] { snapshot }, snapshot.ProviderId);

    /// <summary>Refresca el contenido con todos los snapshots conocidos.</summary>
    public void Update(IEnumerable<UsageSnapshot> snapshots, string? updatedProviderId = null)
    {
        if (updatedProviderId is not null)
            _spinningProviders.Remove(updatedProviderId);
        else
            _spinningProviders.Clear();

        var ordered = snapshots.ToList();

        _refreshRotates.Clear();
        ProvidersHost.Children.Clear();

        if (ordered.Count == 0)
        {
            ProvidersHost.Children.Add(new TextBlock
            {
                Text = Loc.T(Str.TooltipLoading),
                Foreground = new SolidColorBrush(Color.FromRgb(201, 205, 212)),
                FontSize = 12
            });
            return;
        }

        for (int i = 0; i < ordered.Count; i++)
            ProvidersHost.Children.Add(BuildProvider(ordered[i], i == ordered.Count - 1));
    }

    private UIElement BuildProvider(UsageSnapshot snapshot, bool isLast)
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, isLast ? 0 : 14)
        };

        panel.Children.Add(BuildProviderHeader(snapshot));

        switch (snapshot.Status)
        {
            case UsageStatus.NotAuthenticated:
                panel.Children.Add(BuildMessage(snapshot.Message ?? Loc.T(Str.MsgSignIn)));
                break;

            case UsageStatus.Error when snapshot.Windows.Count == 0:
                panel.Children.Add(BuildMessage(snapshot.Message ?? Loc.T(Str.MsgCouldNotFetch)));
                break;

            case UsageStatus.RateLimited:
                panel.Children.Add(BuildMessage(snapshot.Message ?? Loc.T(Str.MsgRateLimitReached)));
                foreach (var w in snapshot.Windows)
                    panel.Children.Add(BuildBar(w));
                break;

            default:
                foreach (var w in snapshot.Windows)
                    panel.Children.Add(BuildBar(w));
                break;
        }

        panel.Children.Add(new TextBlock
        {
            Text = Loc.T(Str.Updated, snapshot.RetrievedAt.ToLocalTime().ToString("HH:mm:ss")),
            Foreground = new SolidColorBrush(Color.FromRgb(110, 118, 129)),
            FontSize = 10,
            Margin = new Thickness(0, 0, 0, isLast ? 0 : 12)
        });

        if (!isLast)
        {
            panel.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(42, 44, 52))
            });
        }

        return panel;
    }

    private UIElement BuildProviderHeader(UsageSnapshot snapshot)
    {
        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var name = new TextBlock
        {
            Text = snapshot.ProviderName,
            Foreground = Brushes.White,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(name, 0);
        header.Children.Add(name);

        var refresh = BuildRefreshButton(snapshot.ProviderId);
        Grid.SetColumn(refresh, 1);
        header.Children.Add(refresh);

        var status = new TextBlock
        {
            Text = StatusLabel(snapshot.Status),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(154, 160, 170)),
            FontSize = 11
        };
        Grid.SetColumn(status, 2);
        header.Children.Add(status);

        return header;
    }

    private Button BuildRefreshButton(string providerId)
    {
        var button = new Button
        {
            Width = 22,
            Height = 22,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = Loc.T(Str.MenuRefreshNow),
            Tag = providerId,
            Content = BuildRefreshGlyph(providerId)
        };
        button.Click += OnRefreshClick;
        return button;
    }

    private Canvas BuildRefreshGlyph(string providerId)
    {
        var rotate = new RotateTransform(0);
        var brush = new SolidColorBrush(Color.FromRgb(154, 160, 170));
        var canvas = new Canvas
        {
            Width = 16,
            Height = 16,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = rotate
        };

        canvas.Children.Add(new Path
        {
            Stroke = brush,
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = Geometry.Parse("M 12.2 4.6 A 5 5 0 1 0 13 8")
        });
        canvas.Children.Add(new Path
        {
            Fill = brush,
            Data = Geometry.Parse("M 9 3 L 13 3 L 13 7 Z")
        });

        _refreshRotates[providerId] = rotate;
        if (_spinningProviders.Contains(providerId))
            StartSpin(providerId);

        return canvas;
    }

    private static string StatusLabel(UsageStatus status) => status switch
    {
        UsageStatus.NotAuthenticated => Loc.T(Str.StatusNoSession),
        UsageStatus.Error => Loc.T(Str.StatusNoConnection),
        UsageStatus.RateLimited => Loc.T(Str.StatusRateLimited),
        _ => ""
    };

    private static UIElement BuildMessage(string text) => new TextBlock
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(201, 205, 212)),
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Margin = new Thickness(0, 0, 0, 8)
    };

    private UIElement BuildBar(UsageWindow w)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

        var header = new Grid();
        header.Children.Add(new TextBlock
        {
            Text = w.Label,
            Foreground = new SolidColorBrush(Color.FromRgb(201, 205, 212)),
            FontSize = 12
        });
        header.Children.Add(new TextBlock
        {
            Text = $"{w.Percent:0}%",
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = Brushes.White,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        });
        panel.Children.Add(header);

        var track = new Border
        {
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromRgb(44, 46, 54)),
            Margin = new Thickness(0, 5, 0, 3),
            ClipToBounds = true
        };
        var fill = new Border
        {
            Height = 8,
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(ColorFor(w.Severity)),
            Width = 0,
            Tag = Math.Clamp(w.Percent, 0, 100)
        };
        track.Child = fill;
        track.Loaded += (_, _) => SetFillWidth(track, fill);
        track.SizeChanged += (_, _) => SetFillWidth(track, fill);
        panel.Children.Add(track);

        panel.Children.Add(new TextBlock
        {
            Text = FormatReset(w.TimeUntilReset),
            Foreground = new SolidColorBrush(Color.FromRgb(110, 118, 129)),
            FontSize = 10
        });

        return panel;
    }

    private static void SetFillWidth(Border track, Border fill)
    {
        if (fill.Tag is double pct && track.ActualWidth > 0)
            fill.Width = track.ActualWidth * (pct / 100.0);
    }

    private static Color ColorFor(UsageSeverity s) => s switch
    {
        UsageSeverity.Critical => Red,
        UsageSeverity.Warning => Yellow,
        UsageSeverity.Normal => Green,
        _ => Gray
    };

    private static string FormatReset(TimeSpan? t)
    {
        if (t is not { } span) return "";
        if (span < TimeSpan.Zero) return Loc.T(Str.ResetsSoon);
        if (span.TotalHours >= 24) return Loc.T(Str.ResetsInDays, (int)span.TotalDays, span.Hours);
        if (span.TotalHours >= 1) return Loc.T(Str.ResetsInHours, (int)span.TotalHours, span.Minutes);
        return Loc.T(Str.ResetsInMinutes, span.Minutes);
    }

    /// <summary>Posiciona el panel sobre el area de notificacion y lo muestra.</summary>
    public void ShowNearTray()
    {
        var area = SystemParameters.WorkArea;
        Show();
        UpdateLayout();

        Left = area.Right - ActualWidth - 8;
        Top = area.Bottom - ActualHeight - 8;

        Activate();
        Topmost = true;
    }

    public void HidePanel() => Hide();

    private void OnDeactivated(object? sender, EventArgs e) => Hide();

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        Hide();
        _openSettings();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string providerId })
            return;

        _spinningProviders.Add(providerId);
        StartSpin(providerId);
        _refreshProvider(providerId);
    }

    private void StartSpin(string providerId)
    {
        if (!_refreshRotates.TryGetValue(providerId, out var rotate))
            return;

        var spin = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromSeconds(0.8),
            RepeatBehavior = RepeatBehavior.Forever
        };
        rotate.BeginAnimation(RotateTransform.AngleProperty, spin);
    }
}
