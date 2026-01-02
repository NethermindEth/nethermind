// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;
using Nethermind.Core;

namespace Nethermind.Crypto
{
    public abstract class ProtectedData<T> : ProtectedData where T : IDisposable
    {
        private const int EntropyMaxLength = 10;
        private const int EntropyMinLength = 5;
        private static readonly TimeSpan MaxSecureTimeSpan = TimeSpan.FromMinutes(10);

        private readonly ICryptoRandom _random;
        private readonly ITimestamper _timestamper;
        private byte[] _entropy;
        private DateTime _timestamp;
        private byte[] _encryptedData;

        protected ProtectedData(byte[] data, string keyStoreDir, ICryptoRandom? random = null, ITimestamper? timestamper = null)
            : base(keyStoreDir)
        {
            _random = random ?? new CryptoRandom();
            _timestamper = timestamper ?? Timestamper.Default;
            Protect(data);
        }

#pragma warning disable CA1416
        private void Protect(byte[] data)
        {
            _entropy = _random.GenerateRandomBytes(_random.NextInt(EntropyMaxLength - EntropyMinLength) + EntropyMinLength);
            _encryptedData = Protect(data, _entropy, DataProtectionScope.CurrentUser);
            _timestamp = _timestamper.UtcNow;
        }

        public T Unprotect()
        {
            var data = Unprotect(_encryptedData, _entropy, DataProtectionScope.CurrentUser);
            CheckReProtect(data);
            return CreateUnprotected(data);
        }
#pragma warning restore CA1416

        protected abstract T CreateUnprotected(byte[] data);

        private void CheckReProtect(byte[] data)
        {
            if (_timestamper.UtcNow - _timestamp > MaxSecureTimeSpan)
            {
                Protect(data);
            }
        }
    }
}
