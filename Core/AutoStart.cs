using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using Desko.Models;
using Microsoft.Win32;

namespace Desko.Core
{
    // Start with Windows through a logon task in Task Scheduler. The Run key would also work, but Windows
    // deliberately launches Run-key apps only after a startup delay; a logon task starts right away.
    internal static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "Desko";
        const string RestoreValueName = "DeskoRestore";
        const string TaskName = "Desko";

        static bool? _enabled;

        // Answering "is autostart on?" means running schtasks.exe, so it is never done on the UI thread at
        // startup: Prime() runs in the background and callers that can't wait use Known (null = not yet).
        public static bool? Known => _enabled;

        public static void Prime() => _ = Enabled;

        public static bool Enabled
        {
            get => _enabled ??= RunValuePresent() || RunSchtasks($"/query /tn \"{TaskName}\"") == 0;
            set
            {
                if (value && Environment.ProcessPath is string exe)
                {
                    if (CreateTask(exe)) DeleteRunValue();
                    else SetRunValue(exe); // Task Scheduler unavailable: fall back to the Run key
                }
                else
                {
                    RunSchtasks($"/delete /tn \"{TaskName}\" /f");
                    DeleteRunValue();
                }
                _enabled = value;
            }
        }

        // Run once per start (off the startup path): moves an older Run-key registration over to the faster
        // logon task, and re-points the task when the exe has been moved so autostart keeps working.
        public static void Maintain()
        {
            if (Environment.ProcessPath is not string exe) return;

            // Autostart registered under the pre-2.0 name (DeskFences): carry it over to Desko.
            bool legacyTask = RunSchtasks($"/query /tn \"{LegacyName.Old}\"") == 0;
            bool legacyRun;
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                legacyRun = run.GetValue(LegacyName.Old) is string;
                run.DeleteValue(LegacyName.Old + "Restore", false);
            }
            if (legacyTask || legacyRun)
            {
                if (CreateTask(exe))
                {
                    RunSchtasks($"/delete /tn \"{LegacyName.Old}\" /f");
                    using var run = Registry.CurrentUser.CreateSubKey(RunKey);
                    run.DeleteValue(LegacyName.Old, false);
                    _enabled = true;
                }
                return;
            }

            if (RunValuePresent())
            {
                if (CreateTask(exe))
                {
                    DeleteRunValue();
                    _enabled = true;
                }
                return;
            }
            // Only when the registered exe is gone: merely running some other copy (an old download, a
            // test build) must not take autostart over from the installed one.
            string? registered = TaskCommand();
            if (registered != null && !File.Exists(registered) &&
                !string.Equals(registered, exe, StringComparison.OrdinalIgnoreCase))
                CreateTask(exe);
        }

        static string? TaskCommand()
        {
            var (code, output) = RunSchtasksWithOutput($"/query /tn \"{TaskName}\" /xml");
            if (code != 0) return null;
            var m = System.Text.RegularExpressions.Regex.Match(output, "<Command>(.*?)</Command>");
            return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim('"') : null;
        }

        static bool CreateTask(string exe)
        {
            string user = SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
            string xml = $"""
                <?xml version="1.0" encoding="UTF-16"?>
                <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                  <RegistrationInfo><Description>Desko: desktop fences</Description></RegistrationInfo>
                  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
                  <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
                  <Settings>
                    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                    <Priority>4</Priority>
                  </Settings>
                  <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(exe)}</Command></Exec></Actions>
                </Task>
                """;
            string file = Path.Combine(Path.GetTempPath(), $"Desko-task-{Guid.NewGuid():N}.xml");
            try
            {
                File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
                return RunSchtasks($"/create /tn \"{TaskName}\" /xml \"{file}\" /f") == 0;
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                return false;
            }
            finally
            {
                try { File.Delete(file); } catch { }
            }
        }

        static int RunSchtasks(string args) => RunSchtasksWithOutput(args).Code;

        static (int Code, string Output) RunSchtasksWithOutput(string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                if (p == null) return (-1, "");
                string output = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                return (p.WaitForExit(15000) ? p.ExitCode : -1, output);
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                return (-1, "");
            }
        }

        static bool RunValuePresent()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }

        static void SetRunValue(string exe)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(ValueName, $"\"{exe}\"");
        }

        static void DeleteRunValue()
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.DeleteValue(ValueName, false);
        }

        // Logon-time "--restore" run, kept only while items are hidden and the app itself won't start at logon.
        public static void SetRestoreEntry(bool on)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                bool present = key.GetValue(RestoreValueName) is string;
                if (on && !present && Environment.ProcessPath is string exe)
                    key.SetValue(RestoreValueName, $"\"{exe}\" --restore");
                else if (!on && present)
                    key.DeleteValue(RestoreValueName, false);
            }
            catch (Exception) { }
        }
    }
}
