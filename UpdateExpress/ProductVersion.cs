using System.Reflection;

namespace UpdateExpress;

internal static class ProductVersion
{
    internal static string Display =>
        typeof(ProductVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? typeof(ProductVersion).Assembly.GetName().Version?.ToString()
        ?? "Unknown";
}
