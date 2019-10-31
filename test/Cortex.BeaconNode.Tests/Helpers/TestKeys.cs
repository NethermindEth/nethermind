using System;
using System.Collections.Generic;
using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.Containers;
using Cortex.Cryptography;

namespace Cortex.BeaconNode.Tests.Helpers
{
    public static class TestKeys
    {
        public static IEnumerable<byte[]> PrivateKeys(TimeParameters timeParameters)
        {
            // Private key is ~255 bits (32 bytes) long
            var privateKeys = Enumerable.Range(0, (int)(ulong)timeParameters.SlotsPerEpoch * 16).Select(x =>
            {
                var key = new Span<byte>(new byte[32]);
                // Key is big endian number, so write Int32 to last 4 bytes.
                BitConverter.TryWriteBytes(key.Slice(28), x + 1);
                if (BitConverter.IsLittleEndian)
                {
                    // And reverse if necessary
                    key.Slice(28).Reverse();
                }
                return key.ToArray();
            });
            return privateKeys;
        }

        public static IEnumerable<BlsPublicKey> PublicKeys(IEnumerable<byte[]> privateKeys)
        {
            return privateKeys.Select(x =>
            {
                var blsParameters = new BLSParameters()
                {
                    PrivateKey = x
                };
                using var bls = BLS.Create(blsParameters);
                var bytes = new Span<byte>(new byte[BlsPublicKey.Length]);
                bls.TryExportBLSPublicKey(bytes, out var bytesWritten);
                return new BlsPublicKey(bytes);
            });
        }
    }
}
