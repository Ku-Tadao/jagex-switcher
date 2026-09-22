using Avalonia;
using System.Runtime.InteropServices;

namespace JagexSwitcher.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--self-check", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                SwitcherService.SelfCheck();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Self-check failed: {ex.Message}");
                return 1;
            }
        }

        using var instanceLock = new Mutex(initiallyOwned: true, @"Local\JagexSwitcher.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox(IntPtr.Zero, "Jagex Switcher is already running.", "Jagex Switcher", 0x40);
            return 0;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}
