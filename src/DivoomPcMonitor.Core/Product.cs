using System.Reflection;

namespace DivoomPcMonitor;

public static class Product
{
    public static readonly string Version = ReadVersion();
    public const string Name = "Divoom PC Monitor";

    private static string ReadVersion()
    {
        var assembly = typeof(Product).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            var text = plus >= 0 ? informational[..plus] : informational;
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        var assemblyVersion = assembly.GetName().Version;
        if (assemblyVersion is null)
        {
            return "0.0.0";
        }

        return assemblyVersion.ToString(3);
    }
}
