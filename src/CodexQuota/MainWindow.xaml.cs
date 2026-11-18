using System.Windows;
using System.Windows.Media;
using System.ComponentModel;
using System.Windows.Threading;

namespace CodexQuota;

public partial class MainWindow : Window
{
    private const int HostConfirmationSamples = 2;
    private readonly DispatcherTimer _hostTimer = new();
    private readonly DispatcherTimer _fallbackRefreshTimer = new();
    private readonly DispatcherTimer _quotaDisplayTimer = new();
    private readonly SemaphoreSlim _hostCheckGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private AppSettings _settings = AppSettings.Default;
    private CodexAppServerClient? _client;
    private QuotaSet? _latestQuotas;
    private HostBounds? _lastAnchorHost;
    private NativeWindowHelper.ForegroundChangedCallback? _foregroundChanged;
    private nint _foregroundHook;
    private bool _hasQuotaSnapshot;
    private int _visibleHostSamples;
    private int _missingHostSamples;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = AppSettings.Load();
        NativeWindowHelper.EnableClickThrough(this);
        _foregroundChanged = OnForegroundChanged;
        _foregroundHook = NativeWindowHelper.WatchForegroundChanges(_foregroundChanged);

        _hostTimer.Interval = TimeSpan.FromSeconds(_settings.HostPollSeconds);
        _hostTimer.Tick += HostTimer_Tick;

        _fallbackRefreshTimer.Interval = TimeSpan.FromSeconds(_settings.FallbackRefreshSeconds);
        _fallbackRefreshTimer.Tick += FallbackRefreshTimer_Tick;

        _quotaDisplayTimer.Interval = TimeSpan.FromSeconds(1);
        _quotaDisplayTimer.Tick += QuotaDisplayTimer_Tick;

        await EnsureHostAndStartAsync();
        if (!_closing)
        {
            _hostTimer.Start();
        }
    }

    private async void HostTimer_Tick(object? sender, EventArgs e)
    {
        await EnsureHostAndStartAsync();
    }

    private async void FallbackRefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshQuotasAsync();
    }

    private void QuotaDisplayTimer_Tick(object? sender, EventArgs e)
    {
        if (_latestQuotas is not { } quotas)
        {
            return;
        }

        UpdateQuotaValues(quotas, updateColors: false);
    }

    private async Task EnsureHostAndStartAsync()
    {
        if (_closing || !await _hostCheckGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (_closing)
            {
                return;
            }

            if (!TryFindDisplayableHost(out var hostBounds))
            {
                _visibleHostSamples = 0;
                _missingHostSamples = Math.Min(_missingHostSamples + 1, HostConfirmationSamples);
                _fallbackRefreshTimer.Stop();

                if (_missingHostSamples < HostConfirmationSamples &&
                    _lastAnchorHost is { } previous &&
                    NativeWindowHelper.IsVisibleHost(previous.WindowHandle))
                {
                    return;
                }

                Opacity = 0;

                if (_missingHostSamples >= HostConfirmationSamples)
                {
                    await SuspendAndWaitAsync();
                }

                return;
            }

            _missingHostSamples = 0;
            _visibleHostSamples = Math.Min(_visibleHostSamples + 1, HostConfirmationSamples);

            PositionAgainstHost(hostBounds);

            if (_visibleHostSamples < HostConfirmationSamples)
            {
                return;
            }

            if (!IsVisible)
            {
                Opacity = 0;
                Show();
            }

            NativeWindowHelper.KeepAboveHost(this, hostBounds.WindowHandle);

            if (_hasQuotaSnapshot)
            {
                Opacity = 1;
            }

            if (_client is not null)
            {
                if (!_fallbackRefreshTimer.IsEnabled)
                {
                    _fallbackRefreshTimer.Start();
                }

                return;
            }

            if (!CodexHost.IsCodexRuntimePresent())
            {
                await SuspendAndWaitAsync();
                return;
            }

            await StartReadOnlyClientAsync();
        }
        finally
        {
            _hostCheckGate.Release();
        }
    }

    private async Task StartReadOnlyClientAsync()
    {
        var client = new CodexAppServerClient();
        client.RateLimitsUpdated += Client_RateLimitsUpdated;

        try
        {
            await client.StartAsync(CancellationToken.None);
            var account = await client.GetAccountAsync(CancellationToken.None);
            if (!account.IsSignedInWithChatGpt)
            {
                await DisposeClientQuietlyAsync(client);
                await SuspendAndWaitAsync();
                return;
            }

            if (_closing)
            {
                await DisposeClientQuietlyAsync(client);
                return;
            }

            _client = client;
            await RefreshQuotasAsync();
            if (!_closing)
            {
                _fallbackRefreshTimer.Start();
            }
        }
        catch
        {
            await DisposeClientQuietlyAsync(client);
            await SuspendAndWaitAsync();
        }
    }

    private void Client_RateLimitsUpdated(object? sender, EventArgs e)
    {
        _ = Dispatcher.InvokeAsync(async () => await RefreshQuotasAsync());
    }

    private async Task RefreshQuotasAsync()
    {
        if (_closing || !await _refreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var client = _client;
            if (_closing || client is null || _visibleHostSamples < HostConfirmationSamples ||
                !TryFindDisplayableHost(out var hostBounds))
            {
                return;
            }

            var quotas = await client.GetRateLimitsAsync(CancellationToken.None);
            ApplyQuotas(quotas);
        }
        catch
        {
            await SuspendAndWaitAsync();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void ApplyQuotas(QuotaSet quotas)
    {
        _latestQuotas = quotas;
        UpdateQuotaValues(quotas, updateColors: true);

        var fiveHour = quotas.FiveHour;
        FiveHourChip.Visibility = fiveHour is null ? Visibility.Collapsed : Visibility.Visible;
        _hasQuotaSnapshot = true;

        if (!_quotaDisplayTimer.IsEnabled)
        {
            _quotaDisplayTimer.Start();
        }

        UpdateLayout();
        if (_visibleHostSamples < HostConfirmationSamples ||
            !TryFindDisplayableHost(out var hostBounds))
        {
            return;
        }

        PositionAgainstHost(hostBounds);
        Opacity = 1;
    }

    private void UpdateQuotaValues(QuotaSet quotas, bool updateColors)
    {
        var now = DateTimeOffset.Now;
        SetChip(
            FiveHourValue,
            FiveHourLabel,
            FiveHourRing,
            quotas.FiveHour,
            isWeek: false,
            now: now,
            updateColors: updateColors);
        SetChip(
            WeekValue,
            WeekLabel,
            WeekRing,
            quotas.Week,
            isWeek: true,
            now: now,
            updateColors: updateColors);
    }

    private static void SetChip(
        System.Windows.Controls.TextBlock value,
        System.Windows.Controls.TextBlock label,
        System.Windows.Shapes.Ellipse ring,
        QuotaWindow? quota,
        bool isWeek,
        DateTimeOffset now,
        bool updateColors)
    {
        if (quota is null)
        {
            value.Text = "—";
            label.Text = isWeek ? "1W" : "5H";
            if (updateColors)
            {
                ring.Visibility = Visibility.Collapsed;
            }
            return;
        }

        var display = QuotaDisplayFormatter.Format(quota.Value, isWeek, now);
        var text = display.Value.EndsWith('%') ? display.Value[..^1] : display.Value;
        if (value.Text != text)
        {
            value.Text = text;
        }

        value.FontSize = text.Length > 5 ? 7 : text.Length > 4 ? 9 : 14;
        var name = isWeek ? "1W" : "5H";
        label.Text = QuotaDisplayFormatter.FormatLabel(name, display.ResetInfo);
        if (updateColors)
        {
            var remaining = quota.Value.RemainingPercent;
            ring.Visibility = remaining == 0 ? Visibility.Collapsed : Visibility.Visible;
            ring.Stroke = new SolidColorBrush(GetQuotaColor(remaining));
            var circumference = Math.PI * 29 / 3;
            ring.StrokeDashArray = remaining == 100
                ? null
                : new DoubleCollection { circumference * remaining / 100, circumference };
        }
    }

    private static Color GetQuotaColor(int remainingPercent)
    {
        return remainingPercent > 60
            ? Color.FromRgb(25, 180, 134)
            : remainingPercent >= 30
                ? Color.FromRgb(245, 158, 11)
                : Color.FromRgb(239, 68, 68);
    }

    private void PositionAgainstHost(HostBounds hostBounds)
    {
        if (hostBounds.SidebarAnchorRectPixels is not { } anchorRect)
        {
            Opacity = 0;
            return;
        }

        var anchorTop = DeviceToDip(
            (int)Math.Round(anchorRect.Left + anchorRect.Width / 2),
            (int)Math.Round(anchorRect.Top));
        Left = anchorTop.X - ActualWidth / 2;
        Top = anchorTop.Y - ActualHeight - 2;
        NativeWindowHelper.KeepAboveHost(this, hostBounds.WindowHandle);
    }

    private bool TryFindDisplayableHost(out HostBounds bounds)
    {
        if (!CodexHost.TryFindDisplayableHostWindow(
                NativeWindowHelper.GetWindowHandle(this), out bounds))
        {
            return false;
        }

        if (bounds.SidebarAnchorRectPixels is not null)
        {
            _lastAnchorHost = bounds;
            return true;
        }

        if (bounds.HasModalDialog && _lastAnchorHost is { } previous &&
            previous.WindowHandle == bounds.WindowHandle &&
            previous.SidebarAnchorRectPixels is { } previousAnchor)
        {
            var anchor = previousAnchor;
            anchor.Offset(bounds.Left - previous.Left, bounds.Bottom - previous.Bottom);
            bounds = bounds with { SidebarAnchorRectPixels = anchor };
            return true;
        }

        return false;
    }

    private Point DeviceToDip(int x, int y)
    {
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        return transform is null ? new Point(x, y) : transform.Value.Transform(new Point(x, y));
    }

    private void OnForegroundChanged(nint hook, uint eventType, nint windowHandle,
        int objectId, int childId, uint eventThread, uint eventTime)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!_closing && IsVisible && Opacity > 0 &&
                _lastAnchorHost is { } host && host.WindowHandle == windowHandle &&
                NativeWindowHelper.GetForegroundWindowHandle() == host.WindowHandle)
            {
                NativeWindowHelper.KeepAboveHost(this, host.WindowHandle);
            }
        });
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        NativeWindowHelper.StopWatchingForegroundChanges(_foregroundHook);
        _foregroundHook = nint.Zero;
        _foregroundChanged = null;
        _hostTimer.Stop();
        _fallbackRefreshTimer.Stop();
        _quotaDisplayTimer.Stop();

        var client = Interlocked.Exchange(ref _client, null);
        if (client is not null)
        {
            await DisposeClientQuietlyAsync(client);
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_closing || !IsVisible ||
            !TryFindDisplayableHost(out var hostBounds))
        {
            return;
        }

        PositionAgainstHost(hostBounds);
    }

    private async Task SuspendAndWaitAsync()
    {
        if (_closing)
        {
            return;
        }

        _fallbackRefreshTimer.Stop();
        _quotaDisplayTimer.Stop();

        var client = Interlocked.Exchange(ref _client, null);
        if (client is not null)
        {
            await DisposeClientQuietlyAsync(client);
        }

        _visibleHostSamples = 0;
        _missingHostSamples = 0;
        _hasQuotaSnapshot = false;
        _latestQuotas = null;
        Opacity = 0;
        Hide();
    }

    private async Task DisposeClientQuietlyAsync(CodexAppServerClient client)
    {
        client.RateLimitsUpdated -= Client_RateLimitsUpdated;
        try
        {
            await client.DisposeAsync();
        }
        catch
        {
        }
    }
}
