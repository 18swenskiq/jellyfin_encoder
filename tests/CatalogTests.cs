using System.Data.Common;
using System.Diagnostics;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SpaceSaver;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

internal static class CatalogTests
{
    public static async Task Run(Action<bool, string> check)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var counter = new QueryCounter();
        var options = new DbContextOptionsBuilder<TestCatalogContext>().UseSqlite(connection).AddInterceptors(counter).Options;
        await using var database = new TestCatalogContext(options);
        await database.Database.EnsureCreatedAsync();
        var library = Enumerable.Range(0, 5000).Select(index => Video($"Movie {index:D5}", $"/offline-network/media/{index}.mkv", index % 2 == 0 ? "h264" : "hevc", index * 100L)).ToList();
        var extras = new[]
        {
            Video("Uppercase HEVC", "/media/upper.mkv", "HEVC", 100),
            Video("Alias H265", "/media/alias.mkv", "h265", 100),
            Video("Alias H.265", "/media/alias2.mkv", "H.265", 100),
            Video("Remote", "https://example.com/video.mp4", "h264", 100),
            Video("Stream link", "/media/movie.STRM", "h264", 100),
            Video("Missing codec", "/offline-network/unknown.mkv", null, null),
            Video("Windows path", @"C:\Media\Windows.mp4", "mpeg4", 250),
            Video("UNC path", @"\\server\share\UNC.mp4", "vp9", 350),
            Video("Virtual", "/media/virtual.mkv", "h264", 100),
            Video("Folder", "/media/folder", "h264", 100),
            Video("Album", "/media/album.flac", "h264", 100),
            Video("Default HEVC stream", "/media/default.mkv", "mjpeg", 100)
        };
        extras[8].IsVirtualItem = true;
        extras[9].IsFolder = true;
        extras[10].MediaType = "Audio";
        extras[11].MediaStreams!.First().IsDefault = false;
        extras[11].MediaStreams!.Add(new MediaStreamInfo { ItemId = extras[11].Id, Item = extras[11], StreamIndex = 1, StreamType = MediaStreamTypeEntity.Video, IsDefault = true, Codec = "hevc" });
        library.AddRange(extras);
        database.AddRange(library);
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();
        counter.Sql.Clear();
        var watch = Stopwatch.StartNew();
        var first = await CatalogQuery.Read(database.Items, "size", true, null, 0, 100, CancellationToken.None);
        Console.WriteLine($"Catalog benchmark: 5,012 cached items, {first.Items.Count} returned, {counter.Sql.Count} SQL queries, {watch.ElapsedMilliseconds} ms (in-memory SQLite; not a server benchmark).");
        check(first.TotalCount == 2503 && first.Items.Count == 100, "catalog filters HEVC aliases, remote links, folders and virtual items in SQL");
        check(first.Items[0].Size == 499800 && first.Items[^1].Size == 480000, "catalog selects largest 100 without loading entire library");
        check(first.TotalBytes == library.Where(item => item.Name!.StartsWith("Movie ") && item.MediaStreams!.First().Codec == "h264").Sum(item => item.Size ?? 0) + 600, "catalog totals cover all matching rows, not just the page");
        check(counter.Sql.Count == 2 && counter.Sql[1].Contains("LIMIT") && counter.Sql[1].Contains("OFFSET"), "catalog executes two SQL queries with database pagination and no per-file lookups");
        check(!database.ChangeTracker.Entries().Any(), "catalog does not hydrate or track library entities");
        var next = await CatalogQuery.Read(database.Items, "size", true, null, 100, 100, CancellationToken.None);
        check(!first.Items.Select(item => item.Id).Intersect(next.Items.Select(item => item.Id)).Any(), "catalog pagination does not repeat first page");
        var search = await CatalogQuery.Read(database.Items, "name", false, "WINDOWS", 0, 100, CancellationToken.None);
        check(search.TotalCount == 1 && search.Items[0].Name == "Windows path", "catalog case-insensitive name search and Windows paths");
        search = await CatalogQuery.Read(database.Items, "codec", false, "share", 0, 100, CancellationToken.None);
        check(search.TotalCount == 1 && search.Items[0].Codec == "vp9", "catalog path search and UNC paths");
        var missing = await CatalogQuery.Read(database.Items, "size", false, "unknown.mkv", 0, 100, CancellationToken.None);
        check(missing.Items.Single().Codec == "unknown" && missing.Items[0].Size == 0, "missing cached metadata listed without touching offline storage");
        counter.Sql.Clear();
        var empty = await CatalogQuery.Read(database.Items, "size", true, "no such movie", 0, 100, CancellationToken.None);
        check(empty.TotalCount == 0 && empty.TotalBytes == 0 && empty.Items.Count == 0 && counter.Sql.Count == 1, "empty catalog uses one aggregate query");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await CatalogQuery.Read(database.Items, "size", true, null, 0, 100, cancelled.Token); check(false, "cancelled catalog request"); }
        catch (OperationCanceledException) { check(true, "catalog honors request cancellation"); }
    }

    private static BaseItemEntity Video(string name, string path, string? codec, long? size)
    {
        var item = new BaseItemEntity { Id = Guid.NewGuid(), Type = "MediaBrowser.Controller.Entities.Movies.Movie", Name = name, Path = path, MediaType = "Video", Size = size, MediaStreams = new List<MediaStreamInfo>() };
        if (codec is not null) item.MediaStreams.Add(new MediaStreamInfo { ItemId = item.Id, Item = item, StreamIndex = 0, StreamType = MediaStreamTypeEntity.Video, Codec = codec, IsDefault = true });
        return item;
    }

    private sealed class QueryCounter : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Sql.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class TestCatalogContext(DbContextOptions<TestCatalogContext> options) : DbContext(options)
    {
        public DbSet<BaseItemEntity> Items => Set<BaseItemEntity>();
        protected override void OnModelCreating(ModelBuilder model)
        {
            // Same catalog columns and relationship as Jellyfin, exercised with a real SQLite provider.
            var items = model.Entity<BaseItemEntity>();
            var keepItem = new HashSet<string> { "Id", "Name", "Path", "MediaType", "Size", "IsFolder", "IsVirtualItem", "MediaStreams" };
            foreach (var property in typeof(BaseItemEntity).GetProperties().Where(property => !keepItem.Contains(property.Name))) items.Ignore(property.Name);
            items.HasKey(item => item.Id);
            var streams = model.Entity<MediaStreamInfo>();
            var keepStream = new HashSet<string> { "ItemId", "Item", "StreamIndex", "StreamType", "Codec", "IsDefault", "IsExternal" };
            foreach (var property in typeof(MediaStreamInfo).GetProperties().Where(property => !keepStream.Contains(property.Name))) streams.Ignore(property.Name);
            streams.HasKey(stream => new { stream.ItemId, stream.StreamIndex });
            streams.HasOne(stream => stream.Item).WithMany(item => item.MediaStreams).HasForeignKey(stream => stream.ItemId);
        }
    }
}
