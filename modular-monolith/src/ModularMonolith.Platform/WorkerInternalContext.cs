using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModularMonolith.Platform;

/// <summary>Defines the internal context header and worker signing-key environment variable.</summary>
public static class WorkerInternalContextConstants
{
    public const string HeaderName = "X-ModularMonolith-Context";
    public const string SigningKeyEnvironmentVariable = "MODULARMONOLITH_WORKER_SIGNING_KEY";
}

/// <summary>Holds the per-run symmetric key used to trust Host-generated worker context.</summary>
public sealed class WorkerSigningKey
{
    private const int KeyLength = 32;

    private WorkerSigningKey(byte[] value)
    {
        if (value.Length != KeyLength)
        {
            throw new ArgumentException("The worker signing key must contain exactly 32 bytes.", nameof(value));
        }

        Value = value;
    }

    internal byte[] Value { get; }

    /// <summary>Creates a new cryptographically random worker signing key.</summary>
    public static WorkerSigningKey Create()
        => new(RandomNumberGenerator.GetBytes(KeyLength));

    /// <summary>Restores a 256-bit signing key from Base64 text.</summary>
    public static WorkerSigningKey FromBase64(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new WorkerSigningKey(Convert.FromBase64String(value));
    }

    /// <summary>Exports the signing key for injection into a worker process.</summary>
    public string ToBase64() => Convert.ToBase64String(Value);
}

/// <summary>Encodes and validates signed security context for Host-to-worker requests.</summary>
public sealed class WorkerInternalContext
{
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(1);
    private readonly WorkerSigningKey _signingKey;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates an encoder and validator with the given signing key.</summary>
    public WorkerInternalContext(WorkerSigningKey signingKey)
        : this(signingKey, TimeProvider.System)
    {
    }

    /// <summary>Creates an encoder and validator with an explicit time source.</summary>
    public WorkerInternalContext(WorkerSigningKey signingKey, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _signingKey = signingKey;
        _timeProvider = timeProvider;
    }

    /// <summary>Encodes the allowlisted claims from a principal into a short-lived signed value.</summary>
    public string Encode(ClaimsPrincipal principal, IEnumerable<string> allowedClaimTypes)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(allowedClaimTypes);
        var allowed = allowedClaimTypes.ToHashSet(StringComparer.Ordinal);
        var issuedAt = _timeProvider.GetUtcNow();
        var payload = new WorkerInternalContextPayload(
            principal.Identity?.IsAuthenticated == true,
            principal.Identity?.AuthenticationType,
            principal.Claims
                .Where(claim => allowed.Contains(claim.Type))
                .Select(claim => new InternalClaim(claim.Type, claim.Value))
                .ToArray(),
            issuedAt.ToUnixTimeSeconds(),
            issuedAt.Add(MaximumLifetime).ToUnixTimeSeconds(),
            Guid.NewGuid().ToString("N"));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            WorkerInternalContextJsonContext.Default.WorkerInternalContextPayload);
        var signature = HMACSHA256.HashData(_signingKey.Value, bytes);
        return $"{Base64UrlEncode(bytes)}.{Base64UrlEncode(signature)}";
    }

    /// <summary>Validates a signed value and creates the corresponding worker principal.</summary>
    public ClaimsPrincipal Decode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var parts = value.Split('.');
        if (parts.Length != 2)
        {
            throw new WorkerInternalContextException("The internal request context has an invalid format.");
        }

        byte[] payloadBytes;
        byte[] signature;
        try
        {
            payloadBytes = Base64UrlDecode(parts[0]);
            signature = Base64UrlDecode(parts[1]);
        }
        catch (FormatException exception)
        {
            throw new WorkerInternalContextException("The internal request context has invalid encoding.", exception);
        }

        var expected = HMACSHA256.HashData(_signingKey.Value, payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(expected, signature))
        {
            throw new WorkerInternalContextException("The internal request context signature is invalid.");
        }

        WorkerInternalContextPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize(
                    payloadBytes,
                    WorkerInternalContextJsonContext.Default.WorkerInternalContextPayload)
                ?? throw new WorkerInternalContextException("The internal request context is empty.");
        }
        catch (JsonException exception)
        {
            throw new WorkerInternalContextException("The internal request context payload is invalid.", exception);
        }

        var now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (payload.ExpiresAt <= now
            || payload.IssuedAt > now + 5
            || payload.ExpiresAt - payload.IssuedAt > MaximumLifetime.TotalSeconds)
        {
            throw new WorkerInternalContextException("The internal request context has expired or is outside its validity window.");
        }

        var identity = new ClaimsIdentity(
            payload.Claims.Select(claim => new Claim(claim.Type, claim.Value)),
            payload.Authenticated ? payload.AuthenticationType : null);
        return new ClaimsPrincipal(identity);
    }

    private static string Base64UrlEncode(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/')
            .PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');
        return Convert.FromBase64String(padded);
    }

}

internal sealed record WorkerInternalContextPayload(
    bool Authenticated,
    string? AuthenticationType,
    InternalClaim[] Claims,
    long IssuedAt,
    long ExpiresAt,
    string Nonce);

internal sealed record InternalClaim(string Type, string Value);

[JsonSerializable(typeof(WorkerInternalContextPayload))]
internal partial class WorkerInternalContextJsonContext : JsonSerializerContext;

/// <summary>Indicates that an internal worker request context is malformed or untrusted.</summary>
public sealed class WorkerInternalContextException : Exception
{
    /// <summary>Creates an exception for an invalid internal worker request context.</summary>
    public WorkerInternalContextException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with the underlying validation failure.</summary>
    public WorkerInternalContextException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
