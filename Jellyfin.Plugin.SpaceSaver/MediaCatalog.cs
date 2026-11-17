using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.SpaceSaver;

public sealed record MediaRow(Guid Id, string Name, string Path, string Codec, long Size, string? BlockedReason);

public sealed class MediaCatalog(ILibraryManager library)
{
    public static bool IsHevc(string? codec) => codec?.ToLowerInvariant() is "hevc" or "h265" or "h.265";
    public Video Get(Guid id) => library.GetItemById(id) as Video ?? throw new InvalidOperationException("Video no longer exists in the library.");
    public IReadOnlyList<MediaRow> List()
    {
        var rows = new List<MediaRow>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        const int batch = 500;
        for (var offset = 0; ; offset += batch)
        {
            var items = library.GetItemList(new InternalItemsQuery
            {
                Recursive = true, IsFolder = false, MediaTypes = [MediaType.Video],
                GroupByPresentationUniqueKey = false, StartIndex = offset, Limit = batch
            });
            foreach (var video in items.OfType<Video>())
            {
                if (string.IsNullOrEmpty(video.Path) || !File.Exists(video.Path) || !seen.Add(video.Path)) continue;
                var codec = video.GetDefaultVideoStream()?.Codec ?? "unknown";
                if (IsHevc(codec)) continue;
                try
                {
                    var info = new FileInfo(video.Path);
                    var reason = video.AdditionalParts.Length > 0 ? "Multi-part videos are not supported." : null;
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) reason = "Symbolic links are not converted.";
                    rows.Add(new(video.Id, video.Name, video.Path, codec, info.Length, reason));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (items.Count < batch) break;
        }
        return rows;
    }
}
