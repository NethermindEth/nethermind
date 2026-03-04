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

        var finalSpec = (IXdcReleaseSpec)specProvider.GetFinalSpec();
        snapshotManager.StoreSnapshot(new Types.Snapshot(builtBlock.Number, builtBlock.Hash!, finalSpec.GenesisMasterNodes));

        return builtBlock;
    }
}
