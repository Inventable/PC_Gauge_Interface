using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Gauge.Protocol;

namespace Gauge.Core;

public sealed record CrashCapsuleReport(
    DateTimeOffset SavedUtc,
    string DeviceIdentity,
    CrashCapsule Capsule);

public sealed class CrashCapsuleReportStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _directory;

    public CrashCapsuleReportStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public bool SaveIfNew(
        string deviceIdentity,
        CrashCapsule capsule,
        DateTimeOffset? savedUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceIdentity);
        ArgumentNullException.ThrowIfNull(capsule);

        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, BuildFileName(deviceIdentity, capsule));
        try
        {
            using var output = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            JsonSerializer.Serialize(
                output,
                new CrashCapsuleReport(
                    savedUtc ?? DateTimeOffset.UtcNow,
                    deviceIdentity,
                    capsule),
                JsonOptions);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
    }

    public static string BuildDeduplicationKey(
        string deviceIdentity,
        CrashCapsule capsule) =>
        string.Join(
            "|",
            deviceIdentity,
            capsule.Generation.ToString(CultureInfo.InvariantCulture),
            capsule.BootId.ToString(CultureInfo.InvariantCulture),
            capsule.EventId.ToString(CultureInfo.InvariantCulture),
            capsule.FileId.ToString(CultureInfo.InvariantCulture));

    private static string BuildFileName(
        string deviceIdentity,
        CrashCapsule capsule)
    {
        var key = BuildDeduplicationKey(deviceIdentity, capsule);
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return $"crash-capsule-{hash}.json";
    }
}
