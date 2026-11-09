using System;
using System.IO;
namespace UpdateExpress;
internal static class AgentLog
{
    internal static readonly string LogFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicroSystems", "Update Express", "agent.log");
    internal static void Write(string message)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!); File.AppendAllText(LogFile, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); }
        catch { /* Logging must never prevent startup. */ }
    }
}
