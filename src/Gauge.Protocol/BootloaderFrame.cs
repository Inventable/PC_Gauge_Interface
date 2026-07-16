namespace Gauge.Protocol;

public sealed record BootloaderFrame(
    BootloaderCommand Command,
    ushort DataLength,
    byte Key1,
    byte Key2,
    uint Address,
    byte[] Payload)
{
    public static BootloaderFrame Create(
        BootloaderCommand command,
        uint address = 0,
        ReadOnlySpan<byte> payload = default,
        byte key1 = 0,
        byte key2 = 0)
    {
        ValidateAddress(address);
        if (payload.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "Payload is too large for the bootloader length field.");
        }

        return new BootloaderFrame(command, (ushort)payload.Length, key1, key2, address, payload.ToArray());
    }

    public static BootloaderFrame CreateReadRequest(BootloaderCommand command, uint address, ushort dataLength)
    {
        ValidateAddress(address);
        return new BootloaderFrame(command, dataLength, 0, 0, address, []);
    }

    private static void ValidateAddress(uint address)
    {
        if (address > 0x00FF_FFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(address), "Bootloader addresses are limited to 24 bits.");
        }
    }
}
