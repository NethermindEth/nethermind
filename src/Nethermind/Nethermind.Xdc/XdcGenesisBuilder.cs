// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc;

public class XdcGenesisBuilder(
    IGenesisBuilder genesisBuilder,
    ISpecProvider specProvider,
    ISnapshotManager snapshotManager
) : IGenesisBuilder
{
    public Block Build()
    {
        Block builtBlock = genesisBuilder.Build();

        IXdcReleaseSpec finalSpec = (IXdcReleaseSpec)specProvider.GetFinalSpec();
        Types.Snapshot snapshot = snapshotManager.CreateInitialSnapshot(builtBlock.Number, builtBlock.Hash!, finalSpec.GenesisMasterNodes);
        snapshotManager.StoreSnapshot(snapshot);

        return builtBlock;
    }
}
