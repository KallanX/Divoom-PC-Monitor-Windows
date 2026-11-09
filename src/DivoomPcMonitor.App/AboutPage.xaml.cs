using System.Diagnostics;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;

namespace DivoomPcMonitor;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        var assembly = typeof(AboutPage).Assembly;
        NameText.Text = Product.Name;
        VersionText.Text = Product.Version;
        DescriptionText.Text = assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description
            ?? "Send PC usage to a Divoom Times Gate or Pixoo64.";
        AuthorText.Text = assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "Keith Carichner Jr";
        CopyrightText.Text = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
            ?? "Copyright (c) 2026 Keith Carichner Jr";
        ArchitectureText.Text = RuntimeInformation.ProcessArchitecture.ToString();
        SdkVersionText.Text = Metadata(assembly, "NetSdkVersion");
        RuntimeVersionText.Text = Environment.Version.ToString(3);
        WindowsAppSdkText.Text = Metadata(assembly, "WindowsAppSdkVersion");
        WindowsSdkBuildToolsText.Text = Metadata(assembly, "WindowsSdkBuildToolsVersion");
        NotifyIconText.Text = ComponentVersion(typeof(TaskbarIcon).Assembly);
        SystemManagementText.Text = ComponentVersion(typeof(ManagementObjectSearcher).Assembly);
        var program = ConfigLocator.ResolveLauncherPath(Environment.ProcessPath);
        ProgramText.Text = string.IsNullOrEmpty(program) ? "Not available" : program;
        FileVersionText.Text = Product.Version;
        if (!string.IsNullOrEmpty(program))
        {
            var info = FileVersionInfo.GetVersionInfo(program);
            if (!string.IsNullOrWhiteSpace(info.FileVersion))
            {
                FileVersionText.Text = info.FileVersion;
            }
        }
    }

    private static string Metadata(Assembly assembly, string key)
    {
        var value = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)?.Value;
        return string.IsNullOrWhiteSpace(value) ? "Not available" : value;
    }

    private static string ComponentVersion(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            var text = (plus >= 0 ? informational[..plus] : informational).Trim();
            if (text.Length > 0)
            {
                return text;
            }
        }

        return assembly.GetName().Version?.ToString(3) ?? "Not available";
    }
}
