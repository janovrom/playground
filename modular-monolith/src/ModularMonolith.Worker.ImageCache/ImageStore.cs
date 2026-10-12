using Dapper;
using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace ModularMonolith.Worker.ImageCache;

public sealed record CachedImage(string ContentType, byte[] Content);

public static class ImageCacheKey
{
    public static bool IsValid(string key)
        => Regex.IsMatch(key, "^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant);
}

public interface IImageStore
{
    Task<CachedImage?> GetAsync(string key, CancellationToken cancellationToken);
    Task PutAsync(string key, string contentType, byte[] content, CancellationToken cancellationToken);
    Task CheckpointAsync(CancellationToken cancellationToken);
}

public sealed class ImageStore(string connectionString) : IImageStore
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA journal_mode=WAL;", cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("""
            CREATE TABLE IF NOT EXISTS images (
                key TEXT PRIMARY KEY,
                content_type TEXT NOT NULL,
                content BLOB NOT NULL,
                created_utc TEXT NOT NULL
            );
            """, cancellationToken: cancellationToken));
    }

    public async Task<CachedImage?> GetAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        var row = await connection.QuerySingleOrDefaultAsync<ImageRow>(
            new CommandDefinition("SELECT content_type AS ContentType, content AS Content FROM images WHERE key = @key", new { key }, cancellationToken: cancellationToken));
        return row is null ? null : new CachedImage(row.ContentType, row.Content);
    }

    public async Task PutAsync(string key, string contentType, byte[] content, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO images (key, content_type, content, created_utc)
            VALUES (@key, @contentType, @content, @createdUtc)
            ON CONFLICT(key) DO UPDATE SET
                content_type = excluded.content_type,
                content = excluded.content,
                created_utc = excluded.created_utc;
            """, new { key, contentType, content, createdUtc = DateTimeOffset.UtcNow.ToString("O") }, cancellationToken: cancellationToken));
    }

    public async Task CheckpointAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken: cancellationToken));
        SqliteConnection.ClearAllPools();
    }

    private sealed record ImageRow(string ContentType, byte[] Content);
}
