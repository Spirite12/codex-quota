using System.Text;
using System.Windows;
using System.Windows.Media;
using System.ComponentModel;
using System.Windows.Threading;

namespace CodexQuota;

public partial class MainWindow : Window
{
    private const int ApprovalGapPixels = 30;
    private const int HostConfirmationSamples = 2;
    private const double VerticalNudgeDip = 2;
    private readonly DispatcherTimer _hostTimer = new();
    private readonly DispatcherTimer _fallbackRefreshTimer = new();
    private readonly SemaphoreSlim _hostCheckGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private AppSettings _settings = AppSettings.Default;
    private CodexAppServerClient? _client;
    private HostBounds? _lastHostBounds;
    private int? _lastApprovalRightPixels;
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

        _hostTimer.Interval = TimeSpan.FromSeconds(_settings.HostPollSeconds);
        _hostTimer.Tick += HostTimer_Tick;

        _fallbackRefreshTimer.Interval = TimeSpan.FromSeconds(_settings.FallbackRefreshSeconds);
        _fallbackRefreshTimer.Tick += FallbackRefreshTimer_Tick;

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

            if (!CodexHost.TryFindVisibleHostWindow(out var hostBounds))
            {
                _visibleHostSamples = 0;
                _missingHostSamples = Math.Min(_missingHostSamples + 1, HostConfirmationSamples);
                _fallbackRefreshTimer.Stop();
                Opacity = 0;

                if (_missingHostSamples >= HostConfirmationSamples)
                {
                    _lastHostBounds = null;
                    _lastApprovalRightPixels = null;
                    await SuspendAndWaitAsync();
                }

                return;
            }

            _missingHostSamples = 0;
            _visibleHostSamples = Math.Min(_visibleHostSamples + 1, HostConfirmationSamples);
            hostBounds = StabilizeHostBounds(hostBounds);
            _lastHostBounds = hostBounds;
            PositionAgainstHost(hostBounds);

            if (_visibleHostSamples < HostConfirmationSamples)
            {
                Opacity = 0;
                return;
            }

            if (!IsVisible)
            {
                Opacity = 0;
                Show();
            }

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
                !CodexHost.TryFindVisibleHostWindow(out _))
            {
                Opacity = 0;
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
        var fiveHour = quotas.FiveHour;
        SetChip(FiveHourValue, FiveHourDot, fiveHour);
        FiveHourChip.Visibility = fiveHour is null ? Visibility.Collapsed : Visibility.Visible;
        ChipGapColumn.Width = fiveHour is null ? new GridLength(0) : new GridLength(6);
        System.Windows.Controls.Grid.SetColumn(WeekChip, fiveHour is null ? 0 : 2);
        SetChip(WeekValue, WeekDot, quotas.Week);
        _hasQuotaSnapshot = true;

        UpdateLayout();
        if (_visibleHostSamples < HostConfirmationSamples ||
            !CodexHost.TryFindVisibleHostWindow(out var hostBounds))
        {
            Opacity = 0;
            return;
        }

        hostBounds = StabilizeHostBounds(hostBounds);
        _lastHostBounds = hostBounds;
        PositionAgainstHost(hostBounds);
        Opacity = 1;
    }

    private static void SetChip(System.Windows.Controls.TextBlock value, System.Windows.Shapes.Ellipse dot, QuotaWindow? quota)
    {
        if (quota is null)
        {
            value.Text = "—";
            dot.Fill = new SolidColorBrush(Color.FromRgb(181, 190, 202));
            return;
        }

        value.Text = $"{quota.Value.RemainingPercent}%";
        dot.Fill = new SolidColorBrush(GetQuotaColor(quota.Value.RemainingPercent));
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
        var hostBottomLeft = DeviceToDip(hostBounds.Left, hostBounds.Bottom);
        var hostBottomRight = DeviceToDip(hostBounds.Right, hostBounds.Bottom);
        var targetLeft = hostBottomLeft.X + ((hostBottomRight.X - hostBottomLeft.X - Width) / 2);
        if (hostBounds.ApprovalRight is { } approvalRightPixels)
        {
            targetLeft = DeviceToDip(approvalRightPixels + ApprovalGapPixels, hostBounds.Bottom).X;
        }

        Left = Math.Min(targetLeft, hostBottomRight.X - Width);
        Top = hostBottomLeft.Y - _settings.BottomInsetPixels - Height - VerticalNudgeDip;
    }

    private HostBounds StabilizeHostBounds(HostBounds hostBounds)
    {
        if (hostBounds.ApprovalRight is { } approvalRight)
        {
            _lastApprovalRightPixels = approvalRight;
            return hostBounds;
        }

        if (_lastApprovalRightPixels is not { } previousApprovalRight || _lastHostBounds is not { } previousHostBounds)
        {
            return hostBounds;
        }

        var hostDeltaX = hostBounds.Left - previousHostBounds.Left;
        return hostBounds with
        {
            ApprovalRight = previousApprovalRight + hostDeltaX
        };
    }

    private Point DeviceToDip(int x, int y)
    {
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        return transform is null ? new Point(x, y) : transform.Value.Transform(new Point(x, y));
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _hostTimer.Stop();
        _fallbackRefreshTimer.Stop();

        var client = Interlocked.Exchange(ref _client, null);
        if (client is not null)
        {
            await DisposeClientQuietlyAsync(client);
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_closing || !IsVisible || !CodexHost.TryFindVisibleHostWindow(out var hostBounds))
        {
            return;
        }

        hostBounds = StabilizeHostBounds(hostBounds);
        _lastHostBounds = hostBounds;
        PositionAgainstHost(hostBounds);
    }

    private async Task SuspendAndWaitAsync()
    {
        if (_closing)
        {
            return;
        }

        _fallbackRefreshTimer.Stop();

        var client = Interlocked.Exchange(ref _client, null);
        if (client is not null)
        {
            await DisposeClientQuietlyAsync(client);
        }

        _visibleHostSamples = 0;
        _missingHostSamples = 0;
        _hasQuotaSnapshot = false;
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
