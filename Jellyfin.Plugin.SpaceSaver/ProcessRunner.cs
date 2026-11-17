using System.Diagnostics;
using System.Text;

namespace Jellyfin.Plugin.SpaceSaver;

public sealed class ProcessRunner
{
    public async Task<string> Run(string executable, IEnumerable<string> arguments, CancellationToken token, Action<string>? progress = null)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        process.Start();
        using var registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var output = new StringBuilder();
        var errors = new StringBuilder();
        async Task Read(StreamReader reader, StringBuilder buffer, bool report)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (report && progress is not null) progress(line);
                else if (buffer.Length < 16 * 1024 * 1024) buffer.AppendLine(line);
                if (!report && buffer.Length > 16000) buffer.Remove(0, buffer.Length - 16000);
            }
        }
        await Task.WhenAll(Read(process.StandardOutput, output, true), Read(process.StandardError, errors, false), process.WaitForExitAsync()).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(executable)} exited with {process.ExitCode}: {errors}");
        return output.ToString();
    }
}
