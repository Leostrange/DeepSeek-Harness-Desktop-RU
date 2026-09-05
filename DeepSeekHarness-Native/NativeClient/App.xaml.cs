using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace DeepSeekHarness.Native;

public partial class App : Application
{
    private Mutex? mutex;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        mutex = new Mutex(true, "DeepSeekHarness.Native.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            mutex = null;

            // Already running: bring the existing window forward and exit.
            var existing = Process.GetProcessesByName("DeepSeekHarness.Native")
                .FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
            if (existing is not null)
            {
                ShowWindow(existing.MainWindowHandle, 9); // SW_RESTORE
                SetForegroundWindow(existing.MainWindowHandle);
            }

            Shutdown();
            return;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (mutex is not null)
        {
            try { mutex.ReleaseMutex(); } catch { }
            mutex.Dispose();
        }
        base.OnExit(e);
    }
}
