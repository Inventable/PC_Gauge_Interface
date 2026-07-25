namespace Gauge.Protocol;

public sealed record V3BchDecodeResult(
    bool IsDecodable,
    byte[] CorrectedPage,
    IReadOnlyList<int> CorrectedBitLocations);

public static class V3Bch16
{
    public const int DataBytes = 233;
    public const int ParityBytes = 22;
    public const int PhysicalBytes = 256;
    public const int CorrectionLimit = 16;

    private const int ParentLength = 2047;
    private const int PhysicalBits = 2040;
    private const int SyndromeCount = 32;

    public static V3BchDecodeResult Decode(ReadOnlySpan<byte> physical)
    {
        if (physical.Length != PhysicalBytes)
        {
            throw new ArgumentException($"A V3 physical page must be {PhysicalBytes} bytes.", nameof(physical));
        }

        var working = physical.ToArray();
        if (working[^1] != 0xFF)
        {
            return new V3BchDecodeResult(false, working, []);
        }

        var syndromes = CalculateSyndromes(working);
        if (SyndromesAreZero(syndromes))
        {
            return new V3BchDecodeResult(true, working, []);
        }

        var locator = new ushort[SyndromeCount + 1];
        var degree = FindLocator(syndromes, locator);
        if (degree is 0 or > CorrectionLimit)
        {
            return new V3BchDecodeResult(false, working, []);
        }

        var corrected = new List<int>(degree);
        var inverseLocation = AlphaPower(8);
        for (var bit = 0; bit < PhysicalBits; bit++)
        {
            if (EvaluateLocator(locator, degree, inverseLocation) == 0)
            {
                working[bit / 8] ^= (byte)(0x80 >> (bit % 8));
                corrected.Add(bit);
                if (corrected.Count > CorrectionLimit)
                {
                    return new V3BchDecodeResult(false, physical.ToArray(), []);
                }
            }

            inverseLocation = GfMultiply(inverseLocation, 2);
        }

        if (corrected.Count != degree || !SyndromesAreZero(CalculateSyndromes(working)))
        {
            return new V3BchDecodeResult(false, physical.ToArray(), []);
        }

        return new V3BchDecodeResult(true, working, corrected);
    }

    private static ushort[] CalculateSyndromes(ReadOnlySpan<byte> page)
    {
        var syndromes = new ushort[SyndromeCount + 1];
        for (ushort order = 1; order <= SyndromeCount; order++)
        {
            var value = AlphaPower((ushort)((order * 2039) % ParentLength));
            var step = AlphaPower((ushort)(ParentLength - order));
            ushort syndrome = 0;
            for (var bit = 0; bit < PhysicalBits; bit++)
            {
                if (((page[bit / 8] >> (7 - (bit % 8))) & 1) != 0)
                {
                    syndrome ^= value;
                }

                value = GfMultiply(value, step);
            }

            syndromes[order] = syndrome;
        }

        return syndromes;
    }

    private static bool SyndromesAreZero(IReadOnlyList<ushort> syndromes)
    {
        for (var index = 1; index <= SyndromeCount; index++)
        {
            if (syndromes[index] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static byte FindLocator(IReadOnlyList<ushort> syndromes, ushort[] locator)
    {
        var previous = new ushort[SyndromeCount + 1];
        byte degree = 0;
        byte shift = 1;
        ushort lastDiscrepancy = 1;
        locator[0] = 1;
        previous[0] = 1;

        for (byte iteration = 0; iteration < SyndromeCount; iteration++)
        {
            var discrepancy = syndromes[iteration + 1];
            for (byte index = 1; index <= degree; index++)
            {
                discrepancy ^= GfMultiply(locator[index], syndromes[iteration + 1 - index]);
            }

            if (discrepancy == 0)
            {
                shift++;
                continue;
            }

            var saved = locator.ToArray();
            var scale = GfMultiply(discrepancy, GfInverse(lastDiscrepancy));
            for (var index = 0; index + shift <= SyndromeCount; index++)
            {
                locator[index + shift] ^= GfMultiply(scale, previous[index]);
            }

            if (2 * degree <= iteration)
            {
                degree = (byte)(iteration + 1 - degree);
                saved.CopyTo(previous, 0);
                lastDiscrepancy = discrepancy;
                shift = 1;
            }
            else
            {
                shift++;
            }
        }

        return degree;
    }

    private static ushort EvaluateLocator(IReadOnlyList<ushort> locator, byte degree, ushort value)
    {
        var result = locator[degree];
        while (degree != 0)
        {
            degree--;
            result = (ushort)(GfMultiply(result, value) ^ locator[degree]);
        }

        return result;
    }

    private static ushort GfMultiply(ushort left, ushort right)
    {
        ushort result = 0;
        while (right != 0)
        {
            if ((right & 1) != 0)
            {
                result ^= left;
            }

            right >>= 1;
            left <<= 1;
            if ((left & 0x0800) != 0)
            {
                left ^= 0x0805;
            }
        }

        return result;
    }

    private static ushort GfPower(ushort value, ushort exponent)
    {
        ushort result = 1;
        while (exponent != 0)
        {
            if ((exponent & 1) != 0)
            {
                result = GfMultiply(result, value);
            }

            value = GfMultiply(value, value);
            exponent >>= 1;
        }

        return result;
    }

    private static ushort AlphaPower(ushort exponent) =>
        GfPower(2, (ushort)(exponent % ParentLength));

    private static ushort GfInverse(ushort value) => GfPower(value, ParentLength - 1);
}
