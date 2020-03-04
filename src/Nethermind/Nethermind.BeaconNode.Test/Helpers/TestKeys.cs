//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using Nethermind.Cryptography;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Crypto;

namespace Nethermind.BeaconNode.Test.Helpers
{
    public static class TestKeys
    {
        private static IList<byte[]> _privateKeys = new List<byte[]>();
        private static IList<BlsPublicKey> _publicKeys = new List<BlsPublicKey>();
        private static IDictionary<BlsPublicKey, byte[]> _testKeys = new Dictionary<BlsPublicKey, byte[]>();

        public static IEnumerable<byte[]> PrivateKeys(TimeParameters timeParameters)
        {
            EnsureKeys(timeParameters);
            return _privateKeys;
        }

        public static IEnumerable<BlsPublicKey> PublicKeys(TimeParameters timeParameters)
        {
            EnsureKeys(timeParameters);
            return _publicKeys;
        }

        public static byte[] PublicKeyToPrivateKey(BlsPublicKey publicKey, TimeParameters timeParameters)
        {
            EnsureKeys(timeParameters);
            var privateKey = _testKeys[publicKey];
            return privateKey;
        }

        private static void EnsureKeys(TimeParameters timeParameters)
        {
            var keysRequired = (int)(ulong)timeParameters.SlotsPerEpoch * 16;
            if (_testKeys.Count == keysRequired)
            {
                return;
            }

            _testKeys.Clear();
            _privateKeys.Clear();
            _publicKeys.Clear();
            // Private key is ~255 bits (32 bytes) long
            for (var keyNumber = 0; keyNumber < keysRequired; keyNumber++)
            {
                var privateKeySpan = new Span<byte>(new byte[32]);
                // Key is big endian number, so write Int32 to last 4 bytes.
                BitConverter.TryWriteBytes(privateKeySpan.Slice(28), keyNumber + 1);
                if (BitConverter.IsLittleEndian)
                {
                    // And reverse if necessary
                    privateKeySpan.Slice(28).Reverse();
                }
                var privateKey = privateKeySpan.ToArray();

                var blsParameters = new BLSParameters()
                {
                    PrivateKey = privateKey
                };
                using var bls = BLS.Create(blsParameters);
                var publicKeyBytes = new byte[BlsPublicKey.Length];
                bls.TryExportBlsPublicKey(publicKeyBytes, out var bytesWritten);
                var publicKey = new BlsPublicKey(publicKeyBytes);

                _privateKeys.Add(privateKey);
                _publicKeys.Add(publicKey);
                _testKeys[publicKey] = privateKey;
            }
        }
    }
}
