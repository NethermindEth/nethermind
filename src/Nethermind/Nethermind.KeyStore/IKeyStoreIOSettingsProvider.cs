// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.KeyStore
{
    public interface IKeyStoreIOSettingsProvider
    {
        string StoreDirectory { get; }

        string GetFileName(Address address);

        string KeyName { get; }
    }
}
