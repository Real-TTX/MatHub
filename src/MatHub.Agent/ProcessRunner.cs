using System.Diagnostics;
using System.Text;

namespace MatHub.Agent;

/// <summary>What a process did: its exit code (null when it was stopped after its time), and what it wrote.</summary>
public sealed record ProcessResult(int? ExitCode, string Output, bool TimedOut);

/// <summary>
/// Runs a program and keeps what it writes (standard output and errors, in the order they come, at most the last 256 KB). A program that takes longer than its
/// time is stopped with everything it started.
/// </summary>
public static class ProcessRunner
{
    private const int MaxOutput = 256 * 1024;

    public static async Task<ProcessResult> RunAsync(string file, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null, Encoding? encoding = null)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
            StandardOutputEncoding = encoding ?? Encoding.UTF8,
            StandardErrorEncoding = encoding ?? Encoding.UTF8,
        };
        foreach (var a in arguments)
        {
            start.ArgumentList.Add(a);
        }

        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[key] = value;
        }

        return await RunAsync(start, timeout, ct);
    }

    /// <summary>Runs a command line as it is (for installers whose arguments are written by a person: <c>/S /D=C:\App</c>).</summary>
    public static async Task<ProcessResult> RunRawAsync(string file, string arguments, TimeSpan timeout, CancellationToken ct, string? workingDirectory = null)
    {
        var start = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
        };
        return await RunAsync(start, timeout, ct);
    }

    private static async Task<ProcessResult> RunAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken ct)
    {
        var output = new StringBuilder();
        var gate = new object();
        void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (gate)
            {
                output.AppendLine(line);
                if (output.Length > MaxOutput * 2)
                {
                    output.Remove(0, output.Length - MaxOutput);
                }
            }
        }

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);

        // the end of the process, not of its output: a child that keeps stdout open (cmd start, a setup that starts the program) must not hold the run
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult();
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new ProcessResult(-1, $"{start.FileName} could not be started: {ex.Message}", false);
        }

        if (process.HasExited)
        {
            exited.TrySetResult();
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await exited.Task.WaitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // it ended in the meantime
            }

            Append(timedOut ? $"MatHub: stopped after {timeout.TotalMinutes:0} minutes." : "MatHub: stopped (the agent stops).");
        }

        // the last lines of the asynchronous readers (a moment: a child may hold the output open)
        process.WaitForExit(2000);
        string text;
        lock (gate)
        {
            text = output.Length > MaxOutput ? "…" + output.ToString(output.Length - MaxOutput, MaxOutput) : output.ToString();
        }

        return new ProcessResult(timedOut || ct.IsCancellationRequested ? null : process.ExitCode, text.TrimEnd(), timedOut);
    }
}
