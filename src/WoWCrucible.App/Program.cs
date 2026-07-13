namespace WoWCrucible.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        CrashLogger.Initialize();
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args.FirstOrDefault()));
    }
}
