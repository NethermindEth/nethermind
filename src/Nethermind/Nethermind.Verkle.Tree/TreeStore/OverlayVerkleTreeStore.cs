// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.Verkle.Tree.TreeStore;

public class OverlayVerkleTreeStore: VerkleTreeStore<PersistEveryBlock>
{
    public OverlayVerkleTreeStore(IReadOnlyDbProvider readOnlyDbProvider, IReadOnlyVerkleTreeStore verkleTreeStore,
        ILogManager logManager)
        : base(readOnlyDbProvider, logManager)
    {

    }

    // TODO: actually implement this

    public void ResetOverrides()
    {

    }
}
