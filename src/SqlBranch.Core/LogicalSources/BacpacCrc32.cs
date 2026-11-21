using System.Buffers.Binary;
using System.Runtime.Intrinsics.Arm;

namespace SqlBranch.Core.LogicalSources;

internal static class BacpacCrc32
{
    private static readonly uint[][] Tables = CreateTables();

    public static uint Update(uint crc, ReadOnlySpan<byte> bytes)
    {
        if (Crc32.Arm64.IsSupported)
        {
            while (bytes.Length >= 8)
            {
                crc = Crc32.Arm64.ComputeCrc32(crc, BinaryPrimitives.ReadUInt64LittleEndian(bytes));
                bytes = bytes[8..];
            }
        }
        else
        {
            // ZIP uses the reversed IEEE polynomial. Process eight bytes per iteration.
            while (bytes.Length >= 8)
            {
                var first = crc ^ BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                var second = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
                crc = Tables[7][(byte)first] ^ Tables[6][(byte)(first >> 8)] ^
                    Tables[5][(byte)(first >> 16)] ^ Tables[4][first >> 24] ^
                    Tables[3][(byte)second] ^ Tables[2][(byte)(second >> 8)] ^
                    Tables[1][(byte)(second >> 16)] ^ Tables[0][second >> 24];
                bytes = bytes[8..];
            }
        }
        foreach (var value in bytes)
        {
            crc = Tables[0][(byte)(crc ^ value)] ^ (crc >> 8);
        }
        return crc;
    }

    private static uint[][] CreateTables()
    {
        const uint polynomial = 0xEDB88320;
        var tables = new uint[8][];
        tables[0] = new uint[256];
        for (uint i = 0; i < 256; ++i)
        {
            var crc = i;
            for (var bit = 0; bit < 8; ++bit)
            {
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : polynomial);
            }
            tables[0][i] = crc;
        }
        for (var slice = 1; slice < tables.Length; ++slice)
        {
            tables[slice] = new uint[256];
            for (var i = 0; i < 256; ++i)
            {
                var crc = tables[slice - 1][i];
                tables[slice][i] = tables[0][(byte)crc] ^ (crc >> 8);
            }
        }
        return tables;
    }
}
