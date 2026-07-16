using System.Security.Cryptography;

namespace Gauge.Core;

public sealed record FirmwareFlashRow(uint Address, byte[] Data)
{
    public bool IsErased => Data.All(value => value == 0xFF);
}

public sealed class BootloaderApplicationImage
{
    public const uint ApplicationStart = 0x0800;
    public const uint FlashEndExclusive = 0x10000;
    public const int RowSize = 64;

    private BootloaderApplicationImage(
        string sourcePath,
        IReadOnlyList<FirmwareFlashRow> rows,
        int explicitProgramBytes,
        int metadataBytes)
    {
        SourcePath = sourcePath;
        Rows = rows;
        ExplicitProgramBytes = explicitProgramBytes;
        MetadataBytes = metadataBytes;
        HighestProgramAddress = rows[^1].Address + (RowSize - 1u);

        var digestBytes = rows.SelectMany(row => row.Data).ToArray();
        Sha256 = Convert.ToHexString(SHA256.HashData(digestBytes));
    }

    public string SourcePath { get; }
    public IReadOnlyList<FirmwareFlashRow> Rows { get; }
    public int ExplicitProgramBytes { get; }
    public int MetadataBytes { get; }
    public uint HighestProgramAddress { get; }
    public string Sha256 { get; }
    public FirmwareFlashRow StartRow => Rows[0];
    public IReadOnlyList<FirmwareFlashRow> DataRows => Rows.Skip(1).Where(row => !row.IsErased).ToArray();

    public static BootloaderApplicationImage LoadOffsetProduction(string path)
    {
        ValidateBuildPath(path);
        return Create(path, IntelHexImage.Load(path));
    }

    public static BootloaderApplicationImage Create(string sourcePath, IntelHexImage hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(hex);

        var programBytes = new SortedDictionary<uint, byte>();
        var metadataBytes = 0;
        foreach (var pair in hex.Bytes)
        {
            if (pair.Key < FlashEndExclusive)
            {
                if (pair.Key < ApplicationStart)
                {
                    throw new InvalidDataException(
                        $"HEX contains bootloader/standalone data at 0x{pair.Key:X6}; application data must start at 0x{ApplicationStart:X4}.");
                }

                programBytes.Add(pair.Key, pair.Value);
                continue;
            }

            if (IsPic18MetadataAddress(pair.Key))
            {
                metadataBytes++;
                continue;
            }

            throw new InvalidDataException($"HEX contains unsupported data at address 0x{pair.Key:X8}.");
        }

        if (programBytes.Count == 0)
        {
            throw new InvalidDataException("HEX contains no PIC application program data.");
        }

        if (!programBytes.TryGetValue(ApplicationStart, out var resetByte) || resetByte == 0xFF)
        {
            throw new InvalidDataException($"HEX does not contain a programmed application reset vector at 0x{ApplicationStart:X4}.");
        }

        var highestRow = programBytes.Keys.Max() & ~(uint)(RowSize - 1);
        var rows = new List<FirmwareFlashRow>();
        for (var address = ApplicationStart; address <= highestRow; address += RowSize)
        {
            var data = Enumerable.Repeat((byte)0xFF, RowSize).ToArray();
            for (var index = 0; index < data.Length; index++)
            {
                if (programBytes.TryGetValue(address + (uint)index, out var value))
                {
                    data[index] = value;
                }
            }

            rows.Add(new FirmwareFlashRow(address, data));
        }

        return new BootloaderApplicationImage(sourcePath, rows, programBytes.Count, metadataBytes);
    }

    private static void ValidateBuildPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var segments = fullPath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (!segments.Contains("Offset", StringComparer.OrdinalIgnoreCase)
            || !segments.Contains("production", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Firmware updates require an Offset/production build artifact.");
        }

        if (segments.Contains("StandAlone", StringComparer.OrdinalIgnoreCase)
            || segments.Contains("Combined", StringComparer.OrdinalIgnoreCase)
            || Path.GetFileName(path).Contains("unified", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("StandAlone, Combined, and unified HEX artifacts are forbidden for bootloader updates.");
        }
    }

    private static bool IsPic18MetadataAddress(uint address)
    {
        return address is >= 0x200000 and <= 0x200007
            or >= 0x300000 and <= 0x30000D;
    }
}
