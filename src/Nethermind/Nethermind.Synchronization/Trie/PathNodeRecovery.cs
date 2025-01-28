// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Tasks;
using Nethermind.Core.Utils;
using Nethermind.State.Healing;
using Nethermind.Trie;

namespace Nethermind.Synchronization.Trie;

public class PathNodeRecovery(NodeDataRecovery nodeDataRecovery, SnapRangeRecovery snapRangeRecovery) : IPathRecovery
{
    public Task<IDictionary<TreePath, byte[]>?> Recover(Hash256 rootHash, Hash256? address, TreePath startingPath, Hash256 startingNodeHash, Hash256 fullPath, CancellationToken cancellationToken = default)
    {
        using AutoCancelTokenSource cts = cancellationToken.CreateChildTokenSource();

        return Wait.ForPassingTask(
            (res) => res != null,
            nodeDataRecovery.Recover(rootHash, address, startingPath, startingNodeHash, fullPath, cts.Token),
            snapRangeRecovery.Recover(rootHash, address, startingPath, startingNodeHash, fullPath, cts.Token)
        );
    }
}
