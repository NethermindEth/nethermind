// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap
{
    public class GetTrieNodesRequest : IDisposable
    {
        public Hash256 RootHash { get; set; } = null!;

        public IOwnedReadOnlyList<PathGroup> AccountAndStoragePaths { get; set; } = null!;

        public void Dispose() => AccountAndStoragePaths?.Dispose();
    }
}
