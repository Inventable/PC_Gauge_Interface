namespace Gauge.Protocol;

public static class BootloaderProtocolConstants
{
    public const byte StartByte = 0x55;
    public const byte CommandSuccess = 0x01;
    public const int HeaderLength = 9;
    public const int VersionPayloadLength = 16;
    public const uint ApplicationStartAddress = 0x800;
}
