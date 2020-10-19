//  Copyright (c) 2020 Demerzel Solutions Limited
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
using System.IO;
using Nethermind.Core;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;

namespace Nethermind.KeyStore
{
    public class PrivateKeyStoreIOSettingsProvider : BaseKeyStoreIOSettingsProvider, IKeyStoreIOSettingsProvider
    {
        private readonly IKeyStoreConfig _config;

        public PrivateKeyStoreIOSettingsProvider(
            IKeyStoreConfig keyStoreConfig)
        {
            _config = keyStoreConfig ?? throw new ArgumentNullException(nameof(keyStoreConfig));
        }

        public string StoreDirectory => GetStoreDirectory(_config.KeyStoreDirectory);

        public string KeyName => "private key";

        public string GetFileName(Address address)
        {
            // "UTC--2018-12-30T14-04-11.699600594Z--1a959a04db22b9f4360db07125f690449fa97a83"
            DateTime utcNow = DateTime.UtcNow;
            return $"UTC--{utcNow:yyyy-MM-dd}T{utcNow:HH-mm-ss.ffffff}000Z--{address.ToString(false, false)}";
        }
    }
}
