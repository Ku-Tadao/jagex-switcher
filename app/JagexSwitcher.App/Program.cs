namespace JagexSwitcher.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--self-check", StringComparer.OrdinalIgnoreCase))
        {
            SwitcherService.SelfCheck();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(new SwitcherService()));
        return 0;
    }
}
