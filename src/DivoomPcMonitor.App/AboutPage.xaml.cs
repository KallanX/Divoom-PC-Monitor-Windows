using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
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
}
