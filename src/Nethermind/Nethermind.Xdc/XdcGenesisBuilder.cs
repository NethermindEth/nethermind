// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Xdc.Spec;
using System;
using System.Collections.Generic;
using System.Linq;

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
