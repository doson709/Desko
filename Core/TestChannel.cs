using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows;
using Desko.Models;

namespace Desko.Core
{
    // Test-only remote control, active only when the DESKO_TEST_PIPE environment variable is set: a named pipe
    // that takes one command per connection and answers with one line (see FenceManager.RunTestCommand). It lets
    // the app be exercised end to end without synthetic mouse or keyboard input. Never active in normal use.
    internal static class TestChannel
    {
        public static void Start(FenceManager manager)
        {
            string? name = Environment.GetEnvironmentVariable("DESKO_TEST_PIPE");
            if (string.IsNullOrEmpty(name)) return;
            new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1);
                        pipe.WaitForConnection();
                        var reader = new StreamReader(pipe);
                        var writer = new StreamWriter(pipe) { AutoFlush = true };
                        string command = reader.ReadLine() ?? "";
                        string reply;
                        try { reply = Application.Current.Dispatcher.Invoke(() => manager.RunTestCommand(command)); }
                        catch (Exception ex) { reply = "error: " + ex; }
                        writer.WriteLine(reply.Replace("\r", "").Replace("\n", " | "));
                        pipe.WaitForPipeDrain();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex);
                        Thread.Sleep(200);
                    }
                }
            })
            { IsBackground = true, Name = "Desko test channel" }.Start();
        }
    }
}
