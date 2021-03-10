//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

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

        public ProtectedData(byte[] data, ICryptoRandom? random = null, ITimestamper? timestamper = null)
        {
            _random = random ?? new CryptoRandom();
            _timestamper = timestamper ?? Timestamper.Default;
            Protect(data);
        }

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
