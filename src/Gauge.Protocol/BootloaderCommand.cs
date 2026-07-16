namespace Gauge.Protocol;

public enum BootloaderCommand : byte
{
    ReadVersion = 0,
    ReadFlash = 1,
    WriteFlash = 2,
    EraseFlash = 3,
    ReadEeprom = 4,
    WriteEeprom = 5,
    ReadConfig = 6,
    WriteConfig = 7,
    CalculateChecksum = 8,
    ResetDevice = 9
}
