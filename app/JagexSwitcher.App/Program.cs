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
            MessageBox.Show(
                "Jagex Switcher is already running.",
                "Jagex Switcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.Message, "Jagex Switcher", MessageBoxButtons.OK, MessageBoxIcon.Error);

        Application.Run(new MainForm(new SwitcherService()));
        return 0;
    }
}
