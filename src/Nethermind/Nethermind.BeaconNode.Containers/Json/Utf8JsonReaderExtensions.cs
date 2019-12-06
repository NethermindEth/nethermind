using System;
using System.Text.Json;

namespace Nethermind.BeaconNode.Containers.Json
{
    public static class Utf8JsonReaderExtensions
    {
        public static byte[] GetBytesFromPrefixedHex(this Utf8JsonReader reader)
        {
            var hex = reader.GetString();
            var bytes = new byte[(hex.Length - 2) / 2];
            var hexIndex = 2;
            for (var byteIndex = 0; byteIndex < bytes.Length; byteIndex++)
            {
                bytes[byteIndex] = Convert.ToByte(hex.Substring(hexIndex, 2), 16);
                hexIndex += 2;
            }
            return bytes;
        }
    }
}
