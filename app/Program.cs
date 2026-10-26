using System;
using Avalonia;

namespace OpenFusionCAD;

public static class Program
{
    [STAThread]
    public static int Main(string[] args) =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Glx, X11RenderingMode.Egl, X11RenderingMode.Software] })
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
}
