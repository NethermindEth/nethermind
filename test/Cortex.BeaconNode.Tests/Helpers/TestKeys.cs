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
                var key = new byte[32];
                var bytes = BitConverter.GetBytes((ulong)(x + 1));
                bytes.CopyTo(key, 0);
                return key;
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
