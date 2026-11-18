using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace ModularMonolith.Worker.ImageCache;

/// <summary>Represents one cached image payload and its media type.</summary>
public sealed record CachedImage(string ContentType, byte[] Content);

/// <summary>Validates identifiers accepted by the image cache.</summary>
public static class ImageCacheKey
{
    /// <summary>Returns whether a cache key contains only supported characters.</summary>
    public static bool IsValid(string key)
        => Regex.IsMatch(key, "^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant);
}

/// <summary>Defines persistence operations for cached worker images.</summary>
public interface IImageStore
{
    /// <summary>Gets one image when its key exists.</summary>
    Task<CachedImage?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>Stores or replaces one image.</summary>
    Task PutAsync(string key, string contentType, byte[] content, CancellationToken cancellationToken);

    /// <summary>Flushes pending SQLite data to durable storage.</summary>
    Task CheckpointAsync(CancellationToken cancellationToken);
}

/// <summary>Stores worker images in a shared SQLite database.</summary>
public sealed class ImageStore(string connectionString) : IImageStore
{
    /// <summary>Creates the image schema and enables write-ahead logging.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS images (
                key TEXT PRIMARY KEY,
                content_type TEXT NOT NULL,
                content BLOB NOT NULL,
                created_utc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Gets one image using a parameterized key query.</summary>
    public async Task<CachedImage?> GetAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT content_type, content FROM images WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CachedImage(reader.GetString(0), (byte[])reader.GetValue(1))
            : null;
    }

    /// <summary>Stores or replaces one image with parameterized values.</summary>
    public async Task PutAsync(string key, string contentType, byte[] content, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO images (key, content_type, content, created_utc)
            VALUES ($key, $contentType, $content, $createdUtc)
            ON CONFLICT(key) DO UPDATE SET
                content_type = excluded.content_type,
                content = excluded.content,
                created_utc = excluded.created_utc;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$contentType", contentType);
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$createdUtc", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Truncates the SQLite write-ahead log and clears pooled connections.</summary>
    public async Task CheckpointAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await command.ExecuteNonQueryAsync(cancellationToken);
        SqliteConnection.ClearAllPools();
    }
}
