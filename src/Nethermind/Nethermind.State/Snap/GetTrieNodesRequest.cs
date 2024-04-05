// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap
{
    public class GetTrieNodesRequest
    {
        public ValueHash256 RootHash { get; set; }

        public IOwnedReadOnlyList<PathGroup> AccountAndStoragePaths { get; set; }
    }
}
