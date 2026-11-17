using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.SpaceSaver;

public sealed class Probe : IDisposable
{
    private readonly JsonDocument document;
    public Probe(string json) => document = JsonDocument.Parse(json);
    public JsonElement[] Streams => document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
    public JsonElement[] Chapters => document.RootElement.TryGetProperty("chapters", out var value) ? value.EnumerateArray().ToArray() : [];
    public double Duration => double.TryParse(Text(document.RootElement.GetProperty("format"), "duration"), CultureInfo.InvariantCulture, out var duration) ? duration : 0;
    public static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.ToString() : "";
    public static string Tag(JsonElement element, string name) => element.TryGetProperty("tags", out var tags) ? Text(tags, name) : "";
    public static bool Cover(JsonElement stream) => stream.TryGetProperty("disposition", out var flags) && Text(flags, "attached_pic") == "1";
    public JsonElement Video => Streams.Single(s => Text(s, "codec_type") == "video" && !Cover(s));
    public void Dispose() => document.Dispose();

    public void CheckInput()
    {
        if (Streams.Count(s => Text(s, "codec_type") == "video" && !Cover(s)) != 1) throw new InvalidOperationException("Exactly one main video stream is required; multi-angle videos are not converted.");
        if (MediaCatalog.IsHevc(Text(Video, "codec_name"))) throw new InvalidOperationException("The file is already HEVC.");
        if (Duration <= 0) throw new InvalidOperationException("Cannot verify this file's duration.");
        if (Text(Video, "color_transfer") is "smpte2084" or "arib-std-b67" || Video.ToString().Contains("DOVI", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("HDR/Dolby Vision conversion is not supported yet; original retained.");
        if (Text(Video, "closed_captions") == "1") throw new InvalidOperationException("Embedded video closed captions require a separate preservation workflow; original retained.");
        if (Text(Video, "field_order") is not ("" or "unknown" or "progressive")) throw new InvalidOperationException("Interlaced video requires deinterlacing and is not supported yet.");
        if (Text(Video, "pix_fmt") is not ("yuv420p" or "yuvj420p" or "nv12" or "yuv420p10le" or "p010le")) throw new InvalidOperationException("Unsupported pixel format; refusing a lossy chroma/bit-depth conversion.");
    }

    public void ValidateOutput(Probe output)
    {
        if (Math.Abs(Duration - output.Duration) > Math.Max(1, Duration * 0.001)) throw new InvalidOperationException("Output duration does not match the source.");
        if (Streams.Length != output.Streams.Length || Chapters.Length != output.Chapters.Length) throw new InvalidOperationException("Output lost streams or chapters.");
        for (var i = 0; i < Streams.Length; i++)
        {
            var source = Streams[i]; var target = output.Streams[i];
            var main = Text(source, "codec_type") == "video" && !Cover(source);
            if (Text(source, "codec_type") != Text(target, "codec_type") || Text(target, "codec_name") != (main ? "hevc" : Text(source, "codec_name")))
                throw new InvalidOperationException($"Stream {i} was lost or changed codec unexpectedly.");
            if (main && Text(source, "pix_fmt").Contains("10", StringComparison.Ordinal) != Text(target, "pix_fmt").Contains("10", StringComparison.Ordinal))
                throw new InvalidOperationException("Video bit depth changed.");
            foreach (var tag in new[] { "language", "title", "filename", "mimetype" })
            {
                var original = Tag(source, tag);
                if (original.Length > 0 && original != "und" && original != Tag(target, tag)) throw new InvalidOperationException($"Stream {i} lost {tag} metadata.");
            }
            if (source.TryGetProperty("disposition", out var flags))
                foreach (var flag in flags.EnumerateObject())
                    if (!target.TryGetProperty("disposition", out var targetFlags) || Text(targetFlags, flag.Name) != flag.Value.ToString()) throw new InvalidOperationException($"Stream {i} changed disposition {flag.Name}.");
            foreach (var field in main ? new[] { "width", "height", "sample_aspect_ratio", "color_range", "color_space", "color_transfer", "color_primaries" } : new[] { "channels", "sample_rate" })
            {
                var original = Text(source, field);
                if (original.Length > 0 && original != "unknown" && original != Text(target, field)) throw new InvalidOperationException($"Stream {i} changed {field}.");
            }
        }
        for (var i = 0; i < Chapters.Length; i++)
        {
            foreach (var field in new[] { "start_time", "end_time" })
                if (Math.Abs(double.Parse(Text(Chapters[i], field), CultureInfo.InvariantCulture) - double.Parse(Text(output.Chapters[i], field), CultureInfo.InvariantCulture)) > 0.05)
                    throw new InvalidOperationException("Chapter timing changed.");
            if (Tag(Chapters[i], "title") != Tag(output.Chapters[i], "title")) throw new InvalidOperationException("Chapter title changed.");
        }
    }
}

public static class EncodingPlan
{
    public static string OutputPath(string source) => Path.GetExtension(source).ToLowerInvariant() is ".mkv" or ".mp4" or ".mov" ? source : Path.ChangeExtension(source, ".mkv");
    public static void ValidateSettings(PluginConfiguration config)
    {
        if (config.Backend is not ("qsv" or "vaapi")) throw new InvalidOperationException("Select Intel QSV or VA-API.");
        if (!config.Device.StartsWith("/dev/dri/renderD", StringComparison.Ordinal) || !config.Device[16..].All(char.IsDigit) || config.Device.Length <= 16) throw new InvalidOperationException("Device must be a render node such as /dev/dri/renderD128.");
        if (config.Quality is < 1 or > 51 || config.MinimumSavingsPercent is < 0 or > 90) throw new InvalidOperationException("Quality must be 1–51 and minimum savings 0–90%.");
    }
    public static List<string> Build(string source, string temporary, Probe probe, PluginConfiguration config)
    {
        ValidateSettings(config);
        var args = new List<string> { "-hide_banner", "-nostdin", "-n", "-loglevel", "warning" };
        var qsv = config.Backend == "qsv";
        args.AddRange(["-init_hw_device", "vaapi=va:" + config.Device]);
        if (qsv) args.AddRange(["-init_hw_device", "qsv=hw@va"]);
        args.AddRange(["-filter_hw_device", qsv ? "hw" : "va"]);
        if (config.HardwareDecode)
        {
            args.AddRange(["-hwaccel", config.Backend, "-hwaccel_device", qsv ? "hw" : "va", "-hwaccel_output_format", config.Backend]);
            if (qsv)
            {
                var decoder = Probe.Text(probe.Video, "codec_name") switch
                {
                    "h264" => "h264_qsv", "mpeg2video" => "mpeg2_qsv", "vc1" => "vc1_qsv",
                    "vp8" => "vp8_qsv", "vp9" => "vp9_qsv", "av1" => "av1_qsv", "mjpeg" => "mjpeg_qsv",
                    _ => throw new InvalidOperationException("No QSV decoder for this codec. Disable hardware decoding to use GPU encoding with CPU decoding.")
                };
                args.AddRange(["-c:V:0", decoder]);
            }
        }
        args.AddRange(["-i", source, "-map", "0", "-map_metadata", "0", "-map_chapters", "0", "-c", "copy"]);
        var videoIndex = Array.FindIndex(probe.Streams, s => Probe.Text(s, "index") == Probe.Text(probe.Video, "index"));
        var spec = videoIndex.ToString(CultureInfo.InvariantCulture);
        args.AddRange(["-c:" + spec, qsv ? "hevc_qsv" : "hevc_vaapi", (qsv ? "-global_quality:" : "-qp:") + spec, config.Quality.ToString(CultureInfo.InvariantCulture)]);
        if (!qsv) args.AddRange(["-rc_mode:" + spec, "CQP"]);
        if (qsv) args.AddRange(["-preset:" + spec, "slow"]);
        var tenBit = Probe.Text(probe.Video, "pix_fmt").Contains("10", StringComparison.Ordinal);
        args.AddRange(["-profile:" + spec, tenBit ? "main10" : "main"]);
        if (!config.HardwareDecode) args.AddRange(["-filter:" + spec, "format=" + (tenBit ? "p010le" : "nv12") + ",hwupload=extra_hw_frames=64"]);
        for (var i = 0; i < probe.Streams.Length; i++)
        {
            var stream = probe.Streams[i];
            if (stream.TryGetProperty("disposition", out var flags))
            {
                var enabled = flags.EnumerateObject().Where(p => p.Value.ToString() == "1").Select(p => p.Name);
                args.AddRange(["-disposition:" + i, string.Join("+", enabled) is { Length: > 0 } value ? value : "0"]);
            }
        }
        var extension = Path.GetExtension(OutputPath(source)).ToLowerInvariant();
        if (extension is ".mp4" or ".mov") args.AddRange(["-tag:" + spec, "hvc1", "-movflags", "+faststart+use_metadata_tags"]);
        args.AddRange(["-max_muxing_queue_size", "4096", "-progress", "pipe:1", "-nostats", "-f", extension == ".mov" ? "mov" : extension == ".mp4" ? "mp4" : "matroska", temporary]);
        return args;
    }
}
