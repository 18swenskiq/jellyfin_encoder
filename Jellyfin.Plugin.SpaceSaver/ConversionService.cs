using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpaceSaver;

public sealed record ConversionJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ItemId { get; init; }
    public string Name { get; init; } = "";
    public string Source { get; init; } = "";
    public string Output { get; init; } = "";
    public string Temporary { get; init; } = "";
    public string Backup { get; init; } = "";
    public string SourceHash { get; init; } = "";
    public string OutputHash { get; init; } = "";
    public string State { get; init; } = "Queued";
    public string Message { get; init; } = "Waiting";
    public double Progress { get; init; }
    public long OriginalBytes { get; init; }
    public long OutputBytes { get; init; }
    public bool BackupDeleted { get; init; }
    public PluginConfiguration Settings { get; init; } = new();
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
}

public sealed class ConversionService : BackgroundService
{
    private readonly MediaCatalog catalog;
    private readonly IMediaEncoder encoder;
    private readonly ILibraryMonitor monitor;
    private readonly ISessionManager sessions;
    private readonly ProcessRunner runner;
    private readonly ILogger<ConversionService> logger;
    private readonly string journal;
    private readonly object gate = new();
    private readonly Dictionary<Guid, ConversionJob> jobs = new();
    private readonly Channel<Guid> queue = Channel.CreateUnbounded<Guid>(new() { SingleReader = true });
    private CancellationTokenSource? activeCancellation;
    private Guid activeId;
    private string? storageError;
    private static bool Pending(ConversionJob job) => job.State is "Queued" or "Encoding" or "Validating" or "Committing";

    public ConversionService(MediaCatalog catalog, IMediaEncoder encoder, ILibraryMonitor monitor, ISessionManager sessions,
        ProcessRunner runner, IApplicationPaths paths, ILogger<ConversionService> logger)
    {
        this.catalog = catalog; this.encoder = encoder; this.monitor = monitor; this.sessions = sessions; this.runner = runner; this.logger = logger;
        var directory = Path.Combine(paths.DataPath, "spacesaver");
        journal = Path.Combine(directory, "jobs.json");
        try
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(journal))
            {
                foreach (var job in JsonSerializer.Deserialize<ConversionJob[]>(File.ReadAllText(journal)) ?? [])
                    jobs[job.Id] = Pending(job) || job.State == "Releasing" ? job with { State = "Interrupted", Message = "Server stopped. No automatic retry or cleanup. Inspect source, backup and temporary paths before retrying." } : job;
                Persist();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            storageError = "Job history is unavailable. Repair " + journal + " and restart Jellyfin. " + ex.Message;
            logger.LogError(ex, "Space Saver disabled because its job history could not be loaded");
        }
    }

    public ConversionJob[] List() { lock (gate) return jobs.Values.OrderByDescending(j => j.CreatedUtc).ToArray(); }
    private void Persist()
    {
        var temp = journal + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, jobs.Values.ToArray());
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, journal, overwrite: true);
    }
    private void Update(Guid id, Func<ConversionJob, ConversionJob> change, bool persist = true)
    {
        lock (gate) { jobs[id] = change(jobs[id]); if (persist) Persist(); }
    }
    public ConversionJob Enqueue(Guid itemId)
    {
        var video = catalog.Get(itemId);
        if (!File.Exists(video.Path) || video.AdditionalParts.Length > 0) throw new InvalidOperationException("Only local single-file videos can be converted.");
        if ((File.GetAttributes(video.Path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Symbolic links are not supported.");
        var config = Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Plugin has not loaded.");
        EncodingPlan.ValidateSettings(config);
        var settings = new PluginConfiguration { Backend = config.Backend, Device = config.Device, Quality = config.Quality, HardwareDecode = config.HardwareDecode, MinimumSavingsPercent = config.MinimumSavingsPercent };
        lock (gate)
        {
            if (storageError is not null) throw new InvalidOperationException(storageError);
            if (jobs.Values.Any(j => Pending(j) && (j.ItemId == itemId || string.Equals(j.Source, video.Path, StringComparison.Ordinal)))) throw new InvalidOperationException("This file is already queued.");
            if (jobs.Values.Count(Pending) >= 500) throw new InvalidOperationException("Queue is full (500 jobs).");
            var output = EncodingPlan.OutputPath(video.Path);
            if (output != video.Path && File.Exists(output)) throw new InvalidOperationException("Output path already exists.");
            var backup = video.Path + ".spacesaver-original";
            if (File.Exists(backup)) throw new InvalidOperationException("A retained original already exists; inspect it before retrying.");
            var id = Guid.NewGuid();
            var job = new ConversionJob { Id = id, ItemId = itemId, Name = video.Name, Source = video.Path, Output = output, Backup = backup,
                Temporary = Path.Combine(Path.GetDirectoryName(output)!, ".spacesaver-" + id.ToString("N") + ".tmp"), Settings = settings };
            jobs.Add(job.Id, job);
            try { Persist(); } catch { jobs.Remove(job.Id); throw; }
            queue.Writer.TryWrite(job.Id);
            return job;
        }
    }
    public void Cancel(Guid id)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var job)) throw new InvalidOperationException("Unknown job.");
            if (job.State == "Queued") { jobs[id] = job with { State = "Cancelled", Message = "Cancelled before encoding." }; Persist(); }
            else if (job.State is "Encoding" or "Validating" && activeId == id) activeCancellation?.Cancel();
            else throw new InvalidOperationException("This job can no longer be cancelled.");
        }
    }
    public async Task DeleteBackup(Guid id, CancellationToken token)
    {
        ConversionJob job;
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out job!) || job.State != "Completed" || job.BackupDeleted) throw new InvalidOperationException("No releasable backup for this job.");
            jobs[id] = job with { State = "Releasing" }; Persist();
        }
        try
        {
            if (await Hash(job.Output, token) != job.OutputHash || await Hash(job.Backup, token) != job.SourceHash) throw new InvalidOperationException("The output or backup changed. Refusing to delete the original.");
            token.ThrowIfCancellationRequested();
            File.Delete(job.Backup);
            Update(id, j => j with { State = "Completed", BackupDeleted = true, Message = "Original deleted; disk space reclaimed." });
        }
        catch { Update(id, j => j with { State = "Completed", Message = "Backup release did not finish. Check paths before retrying." }); throw; }
    }
    private static async Task<string> Hash(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return System.Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
    private async Task<Probe> ProbeFile(string path, CancellationToken token) => new(await runner.Run(encoder.ProbePath,
        ["-v", "error", "-show_streams", "-show_format", "-show_chapters", "-of", "json", path], token));
    private bool Playing(ConversionJob job) => sessions.Sessions.Any(s => s.NowPlayingItem?.Id == job.ItemId || s.NowPlayingItem?.Path == job.Source);

    public async Task<object> Capabilities(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var text = await runner.Run(encoder.EncoderPath, ["-hide_banner", "-encoders"], timeout.Token);
        return new { Ffmpeg = encoder.EncoderPath, Ffprobe = encoder.ProbePath, QsvCompiled = text.Contains("hevc_qsv", StringComparison.Ordinal), VaapiCompiled = text.Contains("hevc_vaapi", StringComparison.Ordinal), DeviceExists = File.Exists(Plugin.Instance!.Configuration.Device), StorageError = storageError, Note = "Compiled encoders do not prove GPU access. Use Test GPU to verify the saved settings." };
    }
    public async Task<object> TestGpu(CancellationToken token)
    {
        var config = Plugin.Instance!.Configuration;
        EncodingPlan.ValidateSettings(config);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-init_hw_device", "vaapi=va:" + config.Device };
        if (config.Backend == "qsv") args.AddRange(["-init_hw_device", "qsv=hw@va"]);
        args.AddRange(["-filter_hw_device", config.Backend == "qsv" ? "hw" : "va", "-f", "lavfi", "-i", "testsrc2=size=128x128:rate=24", "-vf", "format=nv12,hwupload=extra_hw_frames=64", "-frames:v", "24", "-c:v", "hevc_" + config.Backend, "-f", "null", "-"]);
        await runner.Run(encoder.EncoderPath, args, timeout.Token);
        return new { Message = "GPU HEVC encoding succeeded. Source decoding compatibility is checked per conversion." };
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var id in queue.Reader.ReadAllAsync(stoppingToken))
            {
                ConversionJob job;
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                lock (gate)
                {
                    job = jobs[id];
                    if (job.State != "Queued") continue;
                    activeId = id; activeCancellation = cancellation;
                    jobs[id] = job with { State = "Encoding", Message = "Inspecting source and hashing original." }; Persist();
                }
                try { await Convert(job, cancellation.Token); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Space Saver job {JobId} did not complete", id);
                    Update(id, j => j with { State = ex is OperationCanceledException ? "Cancelled" : "Failed", Message = ex.Message });
                }
                finally
                {
                    // Never delete files involved in an interrupted commit; those are recoverable.
                    if (File.Exists(job.Temporary) && !File.Exists(job.Backup))
                    {
                        try { File.Delete(job.Temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "Could not remove temporary output {Path}", job.Temporary); }
                    }
                    lock (gate) { activeId = Guid.Empty; activeCancellation = null; }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (gate) storageError = "Conversion worker stopped; inspect the server log and job files, then restart Jellyfin. " + ex.Message;
            logger.LogError(ex, "Space Saver worker stopped without stopping the Jellyfin host");
        }
    }
    private async Task Convert(ConversionJob job, CancellationToken token)
    {
        if (Playing(job)) throw new InvalidOperationException("This file is currently playing. Retry when playback ends.");
        if (catalog.Get(job.ItemId).Path != job.Source) throw new InvalidOperationException("Library path changed since queueing.");
        using var source = await ProbeFile(job.Source, token);
        source.CheckInput();
        var originalBytes = new FileInfo(job.Source).Length;
        var originalHash = await Hash(job.Source, token);
        var drive = DriveInfo.GetDrives().Where(d => job.Source.StartsWith(Path.TrimEndingDirectorySeparator(d.Name) + Path.DirectorySeparatorChar, StringComparison.Ordinal)).OrderByDescending(d => d.Name.Length).FirstOrDefault()
            ?? new DriveInfo(Path.GetPathRoot(job.Source)!);
        if (drive.AvailableFreeSpace < originalBytes + 256L * 1024 * 1024) throw new InvalidOperationException("Insufficient free space: allow at least the source size plus 256 MiB for the temporary encode.");
        Update(job.Id, j => j with { OriginalBytes = originalBytes, SourceHash = originalHash, Message = "Encoding with " + job.Settings.Backend + (job.Settings.HardwareDecode ? " hardware decoding." : " CPU decoding and GPU encoding.") });
        var args = EncodingPlan.Build(job.Source, job.Temporary, source, job.Settings);
        await runner.Run(encoder.EncoderPath, args, token, line =>
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && double.TryParse(line[12..], CultureInfo.InvariantCulture, out var microseconds))
                Update(job.Id, j => j with { Progress = Math.Clamp(microseconds / 1_000_000 / source.Duration * 100, 0, 99) }, false);
        });
        Update(job.Id, j => j with { State = "Validating", Message = "Checking tracks, metadata, chapters, duration and decoding the full video." });
        using var output = await ProbeFile(job.Temporary, token);
        source.ValidateOutput(output);
        await runner.Run(encoder.EncoderPath, ["-hide_banner", "-nostdin", "-v", "error", "-xerror", "-i", job.Temporary, "-map", "0:V:0", "-an", "-sn", "-f", "null", "-"], token);
        var outputBytes = new FileInfo(job.Temporary).Length;
        if (outputBytes >= originalBytes || outputBytes > originalBytes * (1 - job.Settings.MinimumSavingsPercent / 100d))
        {
            Update(job.Id, j => j with { State = "Skipped", OutputBytes = outputBytes, Message = "Output did not meet minimum savings; original retained." });
            return;
        }
        if (await Hash(job.Source, token) != originalHash) throw new InvalidOperationException("Source changed while encoding; refusing replacement.");
        if (Playing(job)) throw new InvalidOperationException("Playback started while encoding; original retained.");
        var outputHash = await Hash(job.Temporary, token);
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            jobs[job.Id] = jobs[job.Id] with { State = "Committing", OutputBytes = outputBytes, OutputHash = outputHash, Message = "Installing verified output and retaining original backup." };
            Persist();
        }
        monitor.ReportFileSystemChangeBeginning(job.Source);
        try
        {
            OutputInstaller.Install(job.Source, job.Temporary, job.Output, job.Backup);
            Update(job.Id, j => j with { State = "Completed", Progress = 100, Message = "Converted. Original retained; review playback, then delete the backup to reclaim space." });
        }
        finally
        {
            try { monitor.ReportFileSystemChangeComplete(job.Source, true); monitor.ReportFileSystemChanged(job.Output); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not notify Jellyfin of converted media; run a library scan"); }
        }
    }
}
