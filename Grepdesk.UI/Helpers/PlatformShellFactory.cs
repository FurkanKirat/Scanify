using System;
using Grepdesk.Core;
using Grepdesk.Linux;
using Grepdesk.MacOS;
using Grepdesk.Windows;

namespace Grepdesk.UI.Helpers;

public static class PlatformShellFactory
{
    public static IPlatformShell CreatePlatformShell()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsShell();
        if (OperatingSystem.IsLinux())
            return new LinuxShell();
        if (OperatingSystem.IsMacOS())
            return new MacShell();
        
        throw new PlatformNotSupportedException("Unsupported operating system.");
    }
}