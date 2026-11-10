using System;

[assembly: System.Reflection.AssemblyVersion("1.2.2.0")]
[assembly: System.Reflection.AssemblyTitle("Taskbar Widgets")]
[assembly: System.Reflection.AssemblyProduct("Taskbar Widgets")]

namespace TaskbarWidgets
{
    /// <summary>Shared by both widgets and the installer.</summary>
    internal static class AppInfo
    {
        public const string Version = "1.2.2";
        public const string Name = "Taskbar Widgets";
        public const string Repo = "W1nOfGood/taskbar-widgets";
        public const string ZipName = "TaskbarWidgets.zip";
        public static readonly string[] Exes = { "BatteryWidget.exe", "VolumeMediaWidget.exe" };
        public static readonly string[] RunNames = { "TaskbarWidgets.Battery", "TaskbarWidgets.Volume" };
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TaskbarWidgets";
        public const string OwnKey = @"Software\TaskbarWidgets";

        /// <summary>TW_FAKE_VERSION lets you test the updater against the real latest release.</summary>
        public static string Current
        {
            get { string f = Environment.GetEnvironmentVariable("TW_FAKE_VERSION"); return string.IsNullOrEmpty(f) ? Version : f; }
        }
    }
}
