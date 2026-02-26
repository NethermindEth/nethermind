// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Snap
{
    public class GetTrieNodesRequest : IDisposable
    {
        public Hash256 RootHash { get; set; }

        public RlpItemList AccountAndStoragePaths { get; set; }

        public void Dispose() => AccountAndStoragePaths?.Dispose();
    }
}
