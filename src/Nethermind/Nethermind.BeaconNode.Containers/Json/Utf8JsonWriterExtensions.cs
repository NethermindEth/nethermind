using System;
using System.Text.Json;

namespace Nethermind.BeaconNode.Containers.Json
{
    public static class Utf8JsonWriterExtensions
    {
        public static void WritePrefixedHexStringValue(this Utf8JsonWriter writer, ReadOnlySpan<byte> bytes)
        {
            var hex = new char[bytes.Length * 2 + 2];
            hex[0] = '0';
            hex[1] = 'x';
            var hexIndex = 2;
            for (var byteIndex = 0; byteIndex < bytes.Length; byteIndex++)
            {
                var s = bytes[byteIndex].ToString("x2");
                hex[hexIndex] = s[0];
                hex[hexIndex + 1] = s[1];
                hexIndex += 2;
            }
            writer.WriteStringValue(hex);
        }
    }
}
