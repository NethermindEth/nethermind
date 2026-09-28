// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Eez.Test;

/// <summary>Builds blocks with the header derivation gives them, without executing them.</summary>
internal static class DerivedChain
{
    public static readonly ISpecProvider SpecProvider = new TestSpecProvider(London.Instance);

    public static readonly EezSettlementContext Context = new(SyncSettlementFixture.RollupId, SyncSettlementFixture.ChainId, Address.Zero, default, 2);

    public static Block Child(BlockHeader parent, Address beneficiary, byte[] extraData, params Transaction[] transactions)
    {
        BlockHeader header = DerivedHeader.Build(parent, SpecProvider.GetSpec(parent), Context, beneficiary, extraData);
        header.StateRoot = parent.StateRoot ?? Keccak.EmptyTreeHash;
        header.ReceiptsRoot = Keccak.EmptyTreeHash;
        header.Bloom = Bloom.Empty;
        return Build.A.Block.WithHeader(header).WithTransactions(transactions).TestObject;
    }
}
