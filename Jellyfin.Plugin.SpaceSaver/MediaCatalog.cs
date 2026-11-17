using System.Diagnostics;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpaceSaver;

public sealed record MediaRow(Guid Id, string Name, string Path, string Codec, long Size, string? BlockedReason);
public sealed record MediaPage(IReadOnlyList<MediaRow> Items, int TotalCount, long TotalBytes);

public sealed class MediaCatalog(ILibraryManager library, IDbContextFactory<JellyfinDbContext> databases, ILogger<MediaCatalog> logger)
{
    public static bool IsHevc(string? codec) => codec?.ToLowerInvariant() is "hevc" or "h265" or "h.265";
    public Video Get(Guid id) => library.GetItemById(id) as Video ?? throw new InvalidOperationException("Video no longer exists in the library.");

    public async Task<MediaPage> List(string sort, bool descending, string? search, int start, int limit, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        await using var database = await databases.CreateDbContextAsync(token).ConfigureAwait(false);
        var result = await CatalogQuery.Read(database.BaseItems, sort, descending, search, start, limit, token).ConfigureAwait(false);
        logger.LogInformation("Space Saver catalog returned {PageCount} of {TotalCount} items in {ElapsedMilliseconds} ms", result.Items.Count, result.TotalCount, watch.ElapsedMilliseconds);
        return result;
    }
}

/// <summary>Read-only catalog projection: no full library hydration, per-item queries, or media filesystem access.</summary>
public static class CatalogQuery
{
    private sealed class CatalogEntry
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string Path { get; init; } = "";
        public string Codec { get; init; } = "";
        public long Size { get; init; }
    }

    public static async Task<MediaPage> Read(IQueryable<BaseItemEntity> items, string sort, bool descending, string? search, int start, int limit, CancellationToken token)
    {
        // Use cached metadata. Even File.Exists can block on sleeping/unavailable network mounts.
        var videos = items.AsNoTracking().Where(item => item.MediaType == "Video" && !item.IsFolder && !item.IsVirtualItem
            && item.Path != null && item.Path != ""
            && (item.Path.StartsWith("/") || item.Path.StartsWith("\\\\")
                || (item.Path.Length >= 3 && (item.Path.Substring(1, 2) == ":\\" || item.Path.Substring(1, 2) == ":/")))
            && !item.Path.ToLower().EndsWith(".strm"));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            videos = videos.Where(item => (item.Name != null && item.Name.ToLower().Contains(term)) || item.Path!.ToLower().Contains(term));
        }
        var query = videos.Select(item => new CatalogEntry
        {
            Id = item.Id, Name = item.Name ?? "", Path = item.Path!, Size = item.Size ?? 0,
            Codec = item.MediaStreams!.Where(stream => stream.StreamType == MediaStreamTypeEntity.Video && !stream.IsExternal)
                .OrderByDescending(stream => stream.IsDefault).ThenBy(stream => stream.StreamIndex)
                .Select(stream => stream.Codec).FirstOrDefault() ?? "unknown"
        }).Where(item => item.Codec.ToLower() != "hevc" && item.Codec.ToLower() != "h265" && item.Codec.ToLower() != "h.265");
        // Both totals are computed in a single SQL aggregate. Only the requested page is materialized.
        var totals = await query.GroupBy(_ => 1).Select(group => new { Count = group.Count(), Bytes = group.Sum(item => item.Size) })
            .SingleOrDefaultAsync(token).ConfigureAwait(false);
        if (totals is null) return new MediaPage([], 0, 0);
        var ordered = sort switch
        {
            "name" => descending ? query.OrderByDescending(item => item.Name) : query.OrderBy(item => item.Name),
            "codec" => descending ? query.OrderByDescending(item => item.Codec) : query.OrderBy(item => item.Codec),
            _ => descending ? query.OrderByDescending(item => item.Size) : query.OrderBy(item => item.Size)
        };
        var page = await ordered.ThenBy(item => item.Id).Skip(Math.Max(0, start)).Take(Math.Clamp(limit, 1, 500))
            .Select(item => new MediaRow(item.Id, item.Name, item.Path, item.Codec, item.Size, null))
            .ToArrayAsync(token).ConfigureAwait(false);
        return new MediaPage(page, totals.Count, totals.Bytes);
    }
}
