using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DeepSeekHarness.Native;

public partial class MainWindow : Window
{
    private readonly DshHost host = new();
    private string? pendingLatest;
    private CancellationTokenSource? updateCts;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            try
            {
                await host.StartAsync();
            }
            catch (Exception bootEx) when (bootEx is InvalidOperationException or TimeoutException)
            {
                // A failed boot right after an update usually means the new
                // bundle is incompatible — restore the pre-update backup once.
                if (DshUpdater.TryRollback())
                {
                    await host.RestartAsync();
                }
                else throw;
            }
            await WebView.EnsureCoreWebView2Async();
            if (WebView.CoreWebView2 is null)
            {
                throw new InvalidOperationException(
                    "WebView2 не инициализирован. Убедитесь, что установлен WebView2 Runtime.");
            }
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            WebView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            WebView.Source = new Uri(host.BaseUrl);
            Title = $"DeepSeek Harness · {host.BaseUrl}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось запустить DeepSeek Harness: {ex.Message}",
                "DeepSeek Harness", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // Check for updates in the background (non-blocking).
        _ = CheckForUpdateAsync();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        updateCts?.Cancel();
        host.Dispose();
    }

    // ── Update logic ──────────────────────────────────────────────────────

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var (installed, latest) = await DshUpdater.CheckAsync();
            if (string.IsNullOrEmpty(installed) || string.IsNullOrEmpty(latest) || installed == latest)
                return;

            pendingLatest = latest;
            UpdateVersion.Text = $"{installed} → {latest}";
            UpdateBar.Visibility = Visibility.Visible;
        }
        catch { /* offline — ignore */ }
    }

    private void UpdateDismiss_Click(object sender, RoutedEventArgs e)
    {
        UpdateBar.Visibility = Visibility.Collapsed;
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (pendingLatest is null) return;

        UpdateBtn.IsEnabled = false;
        UpdateDismissBtn.IsEnabled = false;
        UpdateProgressTrack.Visibility = Visibility.Visible;
        UpdateDetail.Visibility = Visibility.Visible;
        updateCts = new CancellationTokenSource();

        var progress = new Progress<DshUpdater.UpdateProgress>(p =>
        {
            UpdateStatus.Text = p.Step;
            UpdateDetail.Text = p.Step;
            var maxWidth = UpdateBar.ActualWidth - 200;
            UpdateProgressFill.Width = Math.Max(0, p.Percent / 100.0 * maxWidth);
        });

        try
        {
            var ok = await DshUpdater.UpdateAsync(host.Port, progress, updateCts.Token);
            if (ok)
            {
                UpdateStatus.Text = "Перезапуск…";
                UpdateDetail.Text = "Запуск обновлённой версии…";
                UpdateProgressFill.Width = UpdateBar.ActualWidth - 200;

                await host.RestartAsync();
                WebView.Source = new Uri(host.BaseUrl);
                Title = $"DeepSeek Harness · {host.BaseUrl}";

                UpdateBar.Visibility = Visibility.Collapsed;
                pendingLatest = null;
            }
            else
            {
                UpdateStatus.Text = "Ошибка обновления";
                UpdateDetail.Text = DshUpdater.LastError ?? "Проверьте подключение к интернету и попробуйте снова.";
                UpdateBtn.IsEnabled = true;
                UpdateDismissBtn.IsEnabled = true;
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatus.Text = "Обновление отменено";
            UpdateDetail.Text = "";
            UpdateBtn.IsEnabled = true;
            UpdateDismissBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "Ошибка обновления";
            UpdateDetail.Text = ex.Message;
            UpdateBtn.IsEnabled = true;
            UpdateDismissBtn.IsEnabled = true;
        }
    }

    // Opens the in-harness updater plugin page (/updater) in the WebView.
    private void UpdaterFab_Click(object sender, RoutedEventArgs e)
    {
        var updaterUrl = new UriBuilder(host.BaseUrl) { Path = "/updater" }.Uri;
        WebView.Source = updaterUrl;
    }
}
