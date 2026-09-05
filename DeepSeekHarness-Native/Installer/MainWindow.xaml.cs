using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace DeepSeekHarness.Setup;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        InstallDirBox.Text = InstallerCore.DefaultTarget;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = InstallDirBox.Text };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            InstallDirBox.Text = dlg.SelectedPath;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void SwitchToProgress()
    {
        SettingsPage.Visibility = Visibility.Collapsed;
        ProgressPage.Visibility = Visibility.Visible;
        InstallBtn.Visibility = Visibility.Collapsed;
        CancelBtn.Content = "Закрыть";
        CancelBtn.IsEnabled = false;
    }

    private void SetProgress(double percent, string step, string? detail = null)
    {
        var maxWidth = ProgressPage.ActualWidth;
        ProgressFill.Width = Math.Max(0, percent / 100.0 * maxWidth);
        PercentLabel.Text = $"{(int)percent}%";
        StepLabel.Text = step;
        if (detail != null)
            Log.Text = detail + "\n" + Log.Text;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var target = InstallDirBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            MessageBox.Show("Укажите папку установки.", "DeepSeek Harness",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SwitchToProgress();
        SetProgress(0, "Проверка Node.js...");

        try
        {
            await InstallerCore.EnsureNodeAsync(target, msg => Dispatcher.Invoke(() =>
                SetProgress(5, msg)));

            SetProgress(10, "Распаковка файлов...");

            await Task.Run(() => InstallerCore.Extract(target, (done, total) => Dispatcher.Invoke(() =>
            {
                var pct = total == 0 ? 85 : 10 + done * 75.0 / total;
                SetProgress(pct, "Распаковка файлов...", $"{done} из {total}");
            })));

            SetProgress(88, "Создание ярлыков...");
            InstallerCore.CreateShortcuts(target,
                StartMenuChk.IsChecked == true,
                DesktopChk.IsChecked == true,
                AutostartChk.IsChecked == true);

            SetProgress(100, "Установка завершена!");
            Log.Text = $"Папка: {target}\n" + Log.Text;

            if (LaunchChk.IsChecked == true)
                Process.Start(new ProcessStartInfo(Path.Combine(target, "DeepSeekHarness.cmd"))
                    { UseShellExecute = true });

            CancelBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            SetProgress(0, "Ошибка установки", ex.Message);
            CancelBtn.IsEnabled = true;
        }
    }
}
