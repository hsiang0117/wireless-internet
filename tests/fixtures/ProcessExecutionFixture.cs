// Isolated child executable for ProcessExecutionChecks. Never calls ADB or the network.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

internal static class ProcessExecutionFixture
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length == 0) return 90;
        if (args[0] == "args")
        {
            for (int index = 1; index < args.Length; index++) Console.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(args[index])));
            return 0;
        }
        if (args[0] == "exit") { Console.Write("stdout 中文"); Console.Error.Write("stderr 中文"); return Int32.Parse(args[1]); }
        if (args[0] == "env") { Console.Write(Environment.GetEnvironmentVariable(args[1]) ?? "<absent>"); return 0; }
        if (args[0] == "cwd") { Console.Write(Directory.GetCurrentDirectory()); return 0; }
        if (args[0] == "stdin")
        {
            using (MemoryStream bytes = new MemoryStream())
            {
                Console.OpenStandardInput().CopyTo(bytes);
                Console.Write(Convert.ToBase64String(bytes.ToArray()));
            }
            return 0;
        }
        if (args[0] == "flood")
        {
            int count = Int32.Parse(args[1]);
            Thread output = new Thread(new ThreadStart(delegate { Flood(Console.OpenStandardOutput(), (byte)'A', count); }));
            Thread error = new Thread(new ThreadStart(delegate { Flood(Console.OpenStandardError(), (byte)'B', count); }));
            output.Start(); error.Start(); output.Join(); error.Join();
            return 0;
        }
        if (args[0] == "progress")
        {
            int count = Int32.Parse(args[1]), length = Int32.Parse(args[2]);
            string body = new string('X', length);
            for (int index = 0; index < count; index++)
            {
                Console.WriteLine("OUT" + index + body);
                Console.Error.WriteLine("ERR" + index + body);
            }
            return 0;
        }
        if (args[0] == "phases")
        {
            Console.WriteLine("phase-one"); Console.Out.Flush();
            Thread.Sleep(500);
            Console.Error.WriteLine("phase-two"); Console.Error.Flush();
            Thread.Sleep(300);
            return 0;
        }
        if (args[0] == "record-stall" || args[0] == "hold")
        {
            Record(args[1]);
            Console.WriteLine("owned-fixture-ready"); Console.Out.Flush();
            Thread.Sleep(Int32.Parse(args[2]));
            return 0;
        }
        if (args[0] == "stall") { Thread.Sleep(Int32.Parse(args[1])); return 0; }
        if (args[0] == "fork-exit" || args[0] == "fork-stall")
        {
            string exe = Assembly.GetExecutingAssembly().Location;
            using (Process child = Process.Start(new ProcessStartInfo(exe, "hold \"" + args[1] + "\" 12000") {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe) }))
            {
                Stopwatch ready = Stopwatch.StartNew();
                while (!File.Exists(args[1]) && ready.ElapsedMilliseconds < 2500) Thread.Sleep(10);
                if (!File.Exists(args[1])) return 91;
                Console.WriteLine("parent-ready"); Console.Out.Flush();
                if (args[0] == "fork-stall") Thread.Sleep(12000);
            }
            return 0;
        }
        return 92;
    }

    private static void Flood(Stream destination, byte value, int count)
    {
        byte[] block = new byte[4096];
        for (int index = 0; index < block.Length; index++) block[index] = value;
        while (count > 0) { int take = Math.Min(count, block.Length); destination.Write(block, 0, take); count -= take; }
        destination.Flush();
    }

    private static void Record(string path)
    {
        using (Process self = Process.GetCurrentProcess())
            File.WriteAllText(path, self.Id.ToString(CultureInfo.InvariantCulture) + "|" +
                self.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), new UTF8Encoding(false));
    }
}
