using System;
using System.Linq;
using UpdateExpress.Services;

namespace UpdateExpress;

/// <summary>Custom entry so --service can host SCM without starting WPF.</summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase)))
        {
            try { WindowsServiceHost.Run(); }
            catch (Exception ex)
            {
                try { AgentLog.WriteInstall("Windows service host crashed: " + ex); } catch { /* ignore */ }
                Environment.ExitCode = 1;
            }
            return;
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
