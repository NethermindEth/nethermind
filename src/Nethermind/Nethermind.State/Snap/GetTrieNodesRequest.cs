// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap
{
    public class GetTrieNodesRequest
    {
        public ValueKeccak RootHash { get; set; }

        public PathGroup[] AccountAndStoragePaths { get; set; }
    }
}
