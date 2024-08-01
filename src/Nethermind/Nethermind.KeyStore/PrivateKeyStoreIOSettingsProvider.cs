// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.KeyStore.Config;

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
