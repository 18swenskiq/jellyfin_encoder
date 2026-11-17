using System.Text.Json.Nodes;
using Jellyfin.Plugin.SpaceSaver;
using Microsoft.AspNetCore.Authorization;

var count = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    Console.WriteLine("PASS: " + name); count++;
}
void Reject(Action action, string name)
{
    try { action(); } catch (InvalidOperationException) { Check(true, name); return; }
    throw new Exception("FAILED (not rejected): " + name);
}
var config = new PluginConfiguration();
await CatalogTests.Run(Check);
EncodingPlan.ValidateSettings(config);
Check(MediaCatalog.IsHevc("HEVC") && MediaCatalog.IsHevc("h265") && !MediaCatalog.IsHevc("h264"), "HEVC aliases and H.264 filtering");
foreach (var device in new[] { "/tmp/test", "/dev/dri/renderD", "/dev/dri/renderD128,driver=bad", "/dev/dri/renderD128;touch" })
    Reject(() => EncodingPlan.ValidateSettings(new() { Device = device }), "reject invalid GPU device " + device);
Reject(() => EncodingPlan.ValidateSettings(new() { Quality = 52 }), "reject invalid quality");
Check(EncodingPlan.OutputPath("movie.mp4") == "movie.mp4" && EncodingPlan.OutputPath("movie.avi") == "movie.mkv", "container selection preserves MP4 subtitle support and sidecar basename");
var authorize = (AuthorizeAttribute)Attribute.GetCustomAttribute(typeof(SpaceSaverController), typeof(AuthorizeAttribute))!;
Check(authorize.Policy == MediaBrowser.Common.Api.Policies.RequiresElevation, "all plugin endpoints require administrator access");

const string fixture = """
{"format":{"duration":"3.0"},"streams":[
{"index":0,"codec_type":"video","codec_name":"h264","width":320,"height":180,"pix_fmt":"yuv420p","field_order":"progressive","disposition":{"default":1,"forced":0}},
{"index":1,"codec_type":"audio","codec_name":"aac","channels":2,"sample_rate":"48000","tags":{"language":"eng","title":"Commentary"},"disposition":{"default":0,"forced":0}},
{"index":2,"codec_type":"subtitle","codec_name":"subrip","tags":{"language":"spa","title":"Forced"},"disposition":{"default":0,"forced":1}}],
"chapters":[{"start_time":"0.0","end_time":"3.0","tags":{"title":"Opening"}}]}
""";
using var input = new Probe(fixture);
input.CheckInput();
var outputNode = JsonNode.Parse(fixture)!;
outputNode["streams"]![0]!["codec_name"] = "hevc";
using (var output = new Probe(outputNode.ToJsonString())) { input.ValidateOutput(output); Check(true, "valid stream-copy output accepted"); }
void InvalidOutput(Action<JsonNode> mutate, string name)
{
    var node = outputNode.DeepClone(); mutate(node); using var output = new Probe(node.ToJsonString());
    Reject(() => input.ValidateOutput(output), name);
}
InvalidOutput(n => n["streams"]!.AsArray().RemoveAt(1), "missing audio rejected");
InvalidOutput(n => n["streams"]![1]!["codec_name"] = "opus", "re-encoded audio rejected");
InvalidOutput(n => n["streams"]![2]!["disposition"]!["forced"] = 0, "lost forced subtitle flag rejected");
InvalidOutput(n => n["streams"]![1]!["tags"]!["title"] = "", "lost track title rejected");
InvalidOutput(n => n["format"]!["duration"] = "1.0", "truncated output rejected");
InvalidOutput(n => n["chapters"]![0]!["tags"]!["title"] = "", "lost chapter title rejected");
InvalidOutput(n => n["streams"]![0]!["width"] = 160, "changed resolution rejected");
InvalidOutput(n => n["streams"]![0]!["pix_fmt"] = "yuv420p10le", "changed bit depth rejected");
var hdr = JsonNode.Parse(fixture)!; hdr["streams"]![0]!["color_transfer"] = "smpte2084";
using (var p = new Probe(hdr.ToJsonString())) Reject(p.CheckInput, "HDR rejected before conversion");
var argsQsv = EncodingPlan.Build("movie.mkv", "output.tmp", input, config);
Check(argsQsv.Contains("hevc_qsv") && argsQsv.Contains("h264_qsv") && argsQsv.Contains("qsv=hw@va"), "QSV full hardware encode/decode plan");
Check(argsQsv.Contains("copy") && argsQsv.Contains("-map_chapters") && argsQsv.Contains("-disposition:2"), "explicit stream copy, chapters and dispositions");
var argsVaapi = EncodingPlan.Build("movie.mkv", "output.tmp", input, new() { Backend = "vaapi", HardwareDecode = false });
Check(argsVaapi.Contains("hevc_vaapi") && !argsVaapi.Contains("-hwaccel") && argsVaapi.Any(a => a.Contains("hwupload")), "VA-API upload with CPU decoding");
Check(argsVaapi.Contains("-qp:0") && argsVaapi.Contains("CQP"), "VA-API explicitly selects constant quality rate control");
var argsMp4 = EncodingPlan.Build("movie.mp4", "output.tmp", input, config);
Check(argsMp4.Contains("hvc1") && argsMp4[^2] == "mp4", "MP4 muxer and HEVC tag selected despite temporary suffix");

// Real FFmpeg integration uses software video encoding because CI/development hosts need not have an Intel GPU.
// All stream mapping, metadata, disposition and container arguments come from the production plan.
var runner = new ProcessRunner();
var directory = Path.GetFullPath(Path.Combine("artifacts", "test-media", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(directory);
var installSource = Path.Combine(directory, "install.mkv");
var installTemp = Path.Combine(directory, "install.tmp");
var installBackup = installSource + ".spacesaver-original";
await File.WriteAllTextAsync(installSource, "original"); await File.WriteAllTextAsync(installTemp, "converted");
OutputInstaller.Install(installSource, installTemp, installSource, installBackup);
Check(File.ReadAllText(installSource) == "converted" && File.ReadAllText(installBackup) == "original" && !File.Exists(installTemp), "same-container install retains exact original backup");
await File.WriteAllTextAsync(installTemp, "next output");
try { OutputInstaller.Install(installSource, installTemp, installSource, installBackup); throw new Exception("Overwrote backup"); }
catch (IOException) { Check(File.ReadAllText(installBackup) == "original" && File.ReadAllText(installSource) == "converted", "existing backup blocks replacement"); }
var remuxSource = Path.Combine(directory, "remux.avi");
var remuxOutput = Path.Combine(directory, "remux.mkv");
await File.WriteAllTextAsync(remuxSource, "source"); await File.WriteAllTextAsync(remuxOutput, "collision");
try { OutputInstaller.Install(remuxSource, installTemp, remuxOutput, remuxSource + ".backup"); throw new Exception("Overwrote collision"); }
catch (IOException) { Check(File.ReadAllText(remuxSource) == "source" && File.ReadAllText(remuxOutput) == "collision", "container-change collision leaves source and destination intact"); }
OutputInstaller.Install(remuxSource, installTemp, Path.Combine(directory, "new.mkv"), remuxSource + ".backup");
Check(File.ReadAllText(remuxSource + ".backup") == "source" && !File.Exists(remuxSource), "container-change install retains original");
var rollbackSource = Path.Combine(directory, "rollback.avi");
await File.WriteAllTextAsync(rollbackSource, "rollback original"); await File.WriteAllTextAsync(installTemp, "converted");
try { OutputInstaller.Install(rollbackSource, installTemp, Path.Combine(directory, "missing-directory", "output.mkv"), rollbackSource + ".backup"); throw new Exception("Missing directory accepted"); }
catch (IOException) { Check(File.ReadAllText(rollbackSource) == "rollback original" && !File.Exists(rollbackSource + ".backup"), "failed container-change install rolls original back"); }
var subtitle = Path.Combine(directory, "subtitle.srt");
var metadata = Path.Combine(directory, "chapters.txt");
var attachment = Path.Combine(directory, "attachment.txt");
await File.WriteAllTextAsync(subtitle, "1\n00:00:00,000 --> 00:00:02,500\nPreserve this subtitle.\n");
await File.WriteAllTextAsync(metadata, ";FFMETADATA1\ntitle=Preservation fixture\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=3000\ntitle=Opening\n");
await File.WriteAllTextAsync(attachment, "Test attachment bytes\n");
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var token = timeout.Token;
async Task<Probe> ReadProbe(string path) => new(await runner.Run("ffprobe", ["-v", "error", "-show_streams", "-show_format", "-show_chapters", "-of", "json", path], token));
foreach (var container in new[] { "mkv", "mp4" })
{
    var sourcePath = Path.Combine(directory, "source with spaces & symbols." + container);
    var destination = Path.Combine(directory, "encoded-" + container + ".tmp");
    var generate = new List<string> { "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24:duration=3", "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-f", "lavfi", "-i", "sine=frequency=880:duration=3", "-i", subtitle, "-i", metadata,
        "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:s", "-map_metadata", "4", "-map_chapters", "4", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "16", "-c:a", "aac", "-c:s", container == "mkv" ? "srt" : "mov_text",
        "-metadata:s:a:0", "language=eng", "-metadata:s:a:0", "title=Main", "-metadata:s:a:1", "language=spa", "-metadata:s:a:1", "title=Commentary", "-metadata:s:s:0", "language=eng", "-disposition:a:0", "default", "-disposition:a:1", "0", "-disposition:s:0", "forced" };
    if (container == "mkv") generate.AddRange(["-attach", attachment, "-metadata:s:t:0", "mimetype=text/plain"]);
    generate.Add(sourcePath);
    await runner.Run("ffmpeg", generate, token);
    using var before = await ReadProbe(sourcePath);
    before.CheckInput();
    var production = EncodingPlan.Build(sourcePath, destination, before, config);
    var software = new List<string>();
    for (var i = 0; i < production.Count; i++)
    {
        var option = production[i];
        if (option is "-init_hw_device" or "-filter_hw_device" or "-hwaccel" or "-hwaccel_device" or "-hwaccel_output_format" or "-c:V:0") { i++; continue; }
        if (option.StartsWith("-global_quality:", StringComparison.Ordinal)) { software.AddRange(["-crf", production[++i]]); continue; }
        if (option.StartsWith("-preset:", StringComparison.Ordinal)) { software.AddRange(["-preset", "ultrafast"]); i++; continue; }
        software.Add(option == "hevc_qsv" ? "libx265" : option);
    }
    await runner.Run("ffmpeg", software, token);
    using var after = await ReadProbe(destination);
    before.ValidateOutput(after);
    Check(true, container + " actual HEVC encode retains audio/subtitles/metadata/chapters/attachments");
    foreach (var stream in before.Streams.Where(s => Probe.Text(s, "codec_type") is "audio" or "subtitle"))
    {
        var index = Probe.Text(stream, "index");
        async Task<string> PacketHash(string path) => await runner.Run("ffmpeg", ["-v", "error", "-i", path, "-map", "0:" + index, "-c", "copy", "-f", "hash", "-hash", "sha256", "-"], token);
        Check(await PacketHash(sourcePath) == await PacketHash(destination), container + " stream " + index + " payload unchanged");
    }
    await runner.Run("ffmpeg", ["-v", "error", "-xerror", "-i", destination, "-map", "0:V:0", "-an", "-sn", "-f", "null", "-"], token);
    Check(true, container + " full video decode validation");
}
using (var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
{
    try { await runner.Run("ffmpeg", ["-v", "error", "-re", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=1", "-f", "null", "-"], cancelled.Token); throw new Exception("Cancellation failed"); }
    catch (OperationCanceledException) { Check(true, "cancellation terminates running FFmpeg"); }
}
Console.WriteLine($"{count} checks passed. Fixtures: {directory}");
