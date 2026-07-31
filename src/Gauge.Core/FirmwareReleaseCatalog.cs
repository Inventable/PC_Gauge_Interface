using System.Security.Cryptography;
using System.Text.Json;

namespace Gauge.Core;

public sealed record FirmwareReleaseCatalog(
    int SchemaVersion,
    string Channel,
    string SuiteVersion,
    DateTimeOffset GeneratedUtc,
    IReadOnlyList<FirmwareRelease> Firmware);

public sealed record FirmwareRelease(
    uint DeviceType,
    IReadOnlyList<uint> SupportedPcbs,
    string Version,
    string ImageType,
    string Processor,
    string MinimumBootloader,
    string Url,
    string Sha256,
    string ReleaseNotes = "");

public sealed record FirmwareReleaseCheck(
    FirmwareRelease Release,
    Uri DownloadUri,
    bool IsUpdateAvailable);

public sealed class FirmwareReleaseCatalogClient
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumManifestBytes = 256 * 1024;
    public const int MaximumFirmwareBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;

    public FirmwareReleaseCatalogClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<FirmwareReleaseCheck?> CheckAsync(
        Uri manifestUri,
        uint deviceType,
        uint pcbType,
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        RequireHttps(manifestUri, "Firmware catalog");

        using var response = await _httpClient.GetAsync(
            manifestUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength > MaximumManifestBytes)
        {
            throw new InvalidDataException("Firmware catalog is larger than the allowed 256 KiB.");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var limitedStream = new SizeLimitedReadStream(responseStream, MaximumManifestBytes);
        var catalog = await JsonSerializer.DeserializeAsync<FirmwareReleaseCatalog>(
            limitedStream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Firmware catalog is empty.");

        if (catalog.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported firmware catalog schema {catalog.SchemaVersion}.");
        }

        if (catalog.Firmware is null)
        {
            throw new InvalidDataException("Firmware catalog has no firmware collection.");
        }

        var candidates = catalog.Firmware
            .Where(release => release.DeviceType == deviceType
                && (release.SupportedPcbs is null || release.SupportedPcbs.Count == 0 || release.SupportedPcbs.Contains(pcbType))
                && string.Equals(release.ImageType, "offset-production", StringComparison.OrdinalIgnoreCase)
                && string.Equals(release.Processor, "PIC18F26K80", StringComparison.OrdinalIgnoreCase))
            .Select(release => (Release: release, Version: ParseVersion(release.Version)))
            .OrderByDescending(candidate => candidate.Version)
            .ToArray();

        if (candidates.Length == 0)
        {
            return null;
        }

        var latest = candidates[0];
        ValidateSha256(latest.Release.Sha256);
        var downloadUri = ResolveDownloadUri(latest.Release.Url);
        var installedVersion = ParseVersion(currentVersion);
        return new FirmwareReleaseCheck(latest.Release, downloadUri, latest.Version > installedVersion);
    }

    public async Task<byte[]> DownloadAsync(
        FirmwareReleaseCheck releaseCheck,
        CancellationToken cancellationToken = default)
    {
        RequireHttps(releaseCheck.DownloadUri, "Firmware image");

        using var response = await _httpClient.GetAsync(
            releaseCheck.DownloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength > MaximumFirmwareBytes)
        {
            throw new InvalidDataException("Firmware image is larger than the allowed 4 MiB.");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var limitedStream = new SizeLimitedReadStream(responseStream, MaximumFirmwareBytes);
        using var memory = new MemoryStream();
        await limitedStream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        var bytes = memory.ToArray();
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actualHash, releaseCheck.Release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Downloaded firmware SHA-256 does not match the catalog (expected {releaseCheck.Release.Sha256}, got {actualHash}).");
        }

        return bytes;
    }

    private static Uri ResolveDownloadUri(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidDataException("Firmware catalog entry has no download URL.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var downloadUri))
        {
            throw new InvalidDataException("Firmware catalog entry has an invalid download URL.");
        }

        RequireHttps(downloadUri, "Firmware image");
        return downloadUri;
    }

    private static Version ParseVersion(string value)
    {
        var normalized = value.Trim().TrimStart('v', 'V');
        return Version.TryParse(normalized, out var version)
            ? version
            : throw new InvalidDataException($"Invalid firmware version '{value}' in the release catalog.");
    }

    private static void ValidateSha256(string value)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("Firmware catalog entry has an invalid SHA-256 value.");
        }
    }

    private static void RequireHttps(Uri uri, string description)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException($"{description} must use an absolute HTTPS URL.");
        }
    }

    private sealed class SizeLimitedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maximumBytes;
        private long _bytesRead;

        public SizeLimitedReadStream(Stream inner, long maximumBytes)
        {
            _inner = inner;
            _maximumBytes = maximumBytes;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _bytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            Count(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Count(read);
            return read;
        }

        private void Count(int read)
        {
            _bytesRead += read;
            if (_bytesRead > _maximumBytes)
            {
                throw new InvalidDataException($"Downloaded content exceeds the {_maximumBytes:N0}-byte safety limit.");
            }
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
