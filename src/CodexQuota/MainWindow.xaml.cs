using System.Text;
using System.Windows;
using System.Windows.Media;
using System.ComponentModel;
using System.Windows.Threading;
using CodexQuota.Localization;

namespace CodexQuota;

public partial class MainWindow : Window
{
    private const int HostConfirmationSamples = 2;
    private const double ComposerQuotaLeftOffsetDip = 151;
    private const double ComposerBottomGapDip = 3;
    private const double PlusQuotaLeftOffsetDip = 143;
    private const double VerticalNudgeDip = 2;
    private readonly DispatcherTimer _hostTimer = new();
    private readonly DispatcherTimer _fallbackRefreshTimer = new();
    private readonly DispatcherTimer _quotaDisplayTimer = new();
    private readonly SemaphoreSlim _hostCheckGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private AppSettings _settings = AppSettings.Default;
    private CodexAppServerClient? _client;
    private QuotaSet? _latestQuotas;
    private bool _hasQuotaSnapshot;
    private int _visibleHostSamples;
    private int _missingHostSamples;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        FiveHourLabel.Text = UiText.T("5H：", "5H:");
        WeekLabel.Text = UiText.T("1W：", "1W:");
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = AppSettings.Load();
        NativeWindowHelper.EnableClickThrough(this);

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

            if (!CodexHost.TryFindDisplayableHostWindow(
                    NativeWindowHelper.GetWindowHandle(this),
                    out var hostBounds))
            {
                _visibleHostSamples = 0;
                _missingHostSamples = Math.Min(_missingHostSamples + 1, HostConfirmationSamples);
                _fallbackRefreshTimer.Stop();
                Opacity = 0;

                if (_missingHostSamples >= HostConfirmationSamples)
                {
                    await SuspendAndWaitAsync();
                }

                return;
            }

            _missingHostSamples = 0;
            _visibleHostSamples = Math.Min(_visibleHostSamples + 1, HostConfirmationSamples);

            if (hostBounds.HasImagePreviewOpen)
            {
                Opacity = 0;
                Hide();
                return;
            }

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
                !CodexHost.TryFindDisplayableHostWindow(
                    NativeWindowHelper.GetWindowHandle(this),
                    out var hostBounds))
            {
                Opacity = 0;
                return;
            }

            if (hostBounds.HasImagePreviewOpen)
            {
                Opacity = 0;
                Hide();
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
        ChipGapColumn.Width = fiveHour is null ? new GridLength(0) : new GridLength(6);
        System.Windows.Controls.Grid.SetColumn(WeekChip, fiveHour is null ? 0 : 2);
        _hasQuotaSnapshot = true;

        if (!_quotaDisplayTimer.IsEnabled)
        {
            _quotaDisplayTimer.Start();
        }

        UpdateLayout();
        if (_visibleHostSamples < HostConfirmationSamples ||
            !CodexHost.TryFindDisplayableHostWindow(
                NativeWindowHelper.GetWindowHandle(this),
                out var hostBounds))
        {
            Opacity = 0;
            return;
        }

        if (hostBounds.HasImagePreviewOpen)
        {
            Opacity = 0;
            Hide();
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
            FiveHourResetInfo,
            FiveHourDot,
            quotas.FiveHour,
            isWeek: false,
            now: now,
            updateColors: updateColors);
        SetChip(
            WeekValue,
            WeekResetInfo,
            WeekDot,
            quotas.Week,
            isWeek: true,
            now: now,
            updateColors: updateColors);
    }

    private static void SetChip(
        System.Windows.Controls.TextBlock value,
        System.Windows.Controls.TextBlock resetInfo,
        System.Windows.Shapes.Ellipse dot,
        QuotaWindow? quota,
        bool isWeek,
        DateTimeOffset now,
        bool updateColors)
    {
        if (quota is null)
        {
            value.Text = "—";
            resetInfo.Text = string.Empty;
            resetInfo.Visibility = Visibility.Collapsed;
            if (updateColors)
            {
                dot.Fill = new SolidColorBrush(Color.FromRgb(181, 190, 202));
            }
            return;
        }

        var display = QuotaDisplayFormatter.Format(quota.Value, isWeek, now);
        if (value.Text != display.Value)
        {
            value.Text = display.Value;
        }

        if (resetInfo.Text != display.ResetInfo)
        {
            resetInfo.Text = display.ResetInfo;
        }

        resetInfo.Visibility = string.IsNullOrEmpty(display.ResetInfo) ? Visibility.Collapsed : Visibility.Visible;
        if (updateColors)
        {
            dot.Fill = new SolidColorBrush(GetQuotaColor(quota.Value.RemainingPercent));
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
        var hostBottomLeft = DeviceToDip(hostBounds.Left, hostBounds.Bottom);
        var hostBottomRight = DeviceToDip(hostBounds.Right, hostBounds.Bottom);
        var targetLeft = hostBottomLeft.X + ((hostBottomRight.X - hostBottomLeft.X - Width) / 2);
        var minLeft = hostBottomLeft.X;
        var maxLeft = hostBottomRight.X - Width;

        if (hostBounds.ComposerRectPixels is { } composerRect)
        {
            var composerLeft = DeviceToDip((int)Math.Round(composerRect.Left), (int)Math.Round(composerRect.Bottom)).X;
            var composerRight = DeviceToDip((int)Math.Round(composerRect.Right), (int)Math.Round(composerRect.Bottom)).X;
            var composerWidth = composerRight - composerLeft;
            if (Width <= composerWidth)
            {
                targetLeft = composerLeft + ComposerQuotaLeftOffsetDip;
                minLeft = composerLeft;
                maxLeft = composerRight - Width;
            }
            else
            {
                targetLeft = composerLeft + ((composerWidth - Width) / 2);
            }

            if (hostBounds.PlusRectPixels is { } composerPlusRect)
            {
                var plusCenter = DeviceToDip(
                    (int)Math.Round((composerPlusRect.Left + composerPlusRect.Right) / 2),
                    (int)Math.Round((composerPlusRect.Top + composerPlusRect.Bottom) / 2)).Y;
                Top = plusCenter - (Height / 2);
            }
            else
            {
                var composerBottom = DeviceToDip((int)Math.Round(composerRect.Left), (int)Math.Round(composerRect.Bottom)).Y;
                Top = composerBottom - Height - ComposerBottomGapDip;
            }
        }
        else if (hostBounds.PlusRectPixels is { } plusRect)
        {
            var plusLeft = DeviceToDip((int)Math.Round(plusRect.Left), (int)Math.Round(plusRect.Top)).X;
            targetLeft = plusLeft + PlusQuotaLeftOffsetDip;
            var plusCenter = DeviceToDip(
                (int)Math.Round((plusRect.Left + plusRect.Right) / 2),
                (int)Math.Round((plusRect.Top + plusRect.Bottom) / 2)).Y;
            Top = plusCenter - (Height / 2);
        }
        else
        {
            Top = hostBottomLeft.Y - _settings.BottomInsetPixels - Height - VerticalNudgeDip;
        }

        Left = maxLeft < minLeft ? minLeft : Math.Clamp(targetLeft, minLeft, maxLeft);
        NativeWindowHelper.KeepAboveHost(this, hostBounds.WindowHandle);
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
            !CodexHost.TryFindDisplayableHostWindow(
                NativeWindowHelper.GetWindowHandle(this),
                out var hostBounds))
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
