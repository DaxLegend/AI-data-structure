using System.Globalization;
using System.Text;

namespace AI.DataStructures.Benchmarks;

/// <summary>
/// Mirrors BenchmarkDotNet's console output to a timestamped UTF-8 log file,
/// while keeping the normal interactive console output.
/// </summary>
internal static class BenchmarkConsoleLog
{
    public static void Run(Action benchmarkAction)
    {
        ArgumentNullException.ThrowIfNull(benchmarkAction);

        string logDirectory = Path.Combine("BenchmarkDotNet.Artifacts", "console-logs");
        Directory.CreateDirectory(logDirectory);

        string fileName = $"benchmark-{DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}.log";
        string logPath = Path.GetFullPath(Path.Combine(logDirectory, fileName));

        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;

        using var fileWriter = new StreamWriter(
            logPath,
            append: false,
            encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };

        var teeWriter = new TeeTextWriter(originalOut, fileWriter);

        Console.SetOut(teeWriter);
        Console.SetError(teeWriter);

        try
        {
            Console.WriteLine($"Full benchmark console log: {logPath}");
            Console.WriteLine();

            benchmarkAction();
        }
        finally
        {
            teeWriter.Flush();
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed class TeeTextWriter(TextWriter consoleWriter, TextWriter fileWriter) : TextWriter
    {
        private readonly object _sync = new();

        public override Encoding Encoding => fileWriter.Encoding;

        public override void Write(char value)
        {
            lock (_sync)
            {
                consoleWriter.Write(value);
                fileWriter.Write(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_sync)
            {
                consoleWriter.Write(value);
                fileWriter.Write(value);
            }
        }

        public override void Write(char[] buffer, int index, int count)
        {
            lock (_sync)
            {
                consoleWriter.Write(buffer, index, count);
                fileWriter.Write(buffer, index, count);
            }
        }

        public override void WriteLine()
        {
            lock (_sync)
            {
                consoleWriter.WriteLine();
                fileWriter.WriteLine();
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_sync)
            {
                consoleWriter.WriteLine(value);
                fileWriter.WriteLine(value);
            }
        }

        public override void Flush()
        {
            lock (_sync)
            {
                consoleWriter.Flush();
                fileWriter.Flush();
            }
        }
    }
}
