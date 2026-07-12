namespace Gauge.Protocol;

public static class GaugeProtocolConstants
{
    public const byte StartByte = 0x55;
    public const int HeaderLength = 7;
    public const int CrcLength = 2;
    public const int MaxMemoryGaugePayloadLength = 2048;
    public const int MaxAcousticGaugePayloadLength = 1024;
}

