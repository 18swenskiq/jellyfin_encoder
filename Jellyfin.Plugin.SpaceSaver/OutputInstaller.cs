namespace Jellyfin.Plugin.SpaceSaver;

/// <summary>Installs a verified output while retaining a recoverable original on the same filesystem.</summary>
public static class OutputInstaller
{
    public static void Install(string source, string temporary, string output, string backup)
    {
        if (File.Exists(backup)) throw new IOException("Original backup path already exists; refusing to overwrite it.");
        if (!File.Exists(source) || !File.Exists(temporary)) throw new IOException("Source or verified temporary output is missing.");
        if (source != output && File.Exists(output)) throw new IOException("Output path already exists; refusing to overwrite it.");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(source));
        // Push output bytes to storage before the journaled replacement step.
        using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.None)) stream.Flush(flushToDisk: true);
        if (source == output) File.Replace(temporary, source, backup);
        else
        {
            File.Move(source, backup);
            try { File.Move(temporary, output); }
            catch { File.Move(backup, source); throw; }
        }
    }
}
