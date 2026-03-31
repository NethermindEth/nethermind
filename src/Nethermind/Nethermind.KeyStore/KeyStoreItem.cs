// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.KeyStore
{
    public class KeyStoreItem
    {
        public int Version { get; set; }

        public string Id { get; set; }

        public string Address { get; set; }

        public Crypto Crypto { get; set; }
    }
}
