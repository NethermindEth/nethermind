// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.Healing;

public interface IPathRecovery
{
    Task<IOwnedReadOnlyList<(TreePath, byte[])>?> Recover(Hash256 rootHash, Hash256? address, TreePath startingPath, Hash256 startingNodeHash, Hash256 fullPath, CancellationToken cancellationToken = default);
}
