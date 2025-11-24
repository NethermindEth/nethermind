// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Specs;
using Nethermind.State;

namespace Nethermind.Core.Test.Blockchain;

/// <summary>
/// For when you need blocks with state roots modified.
/// </summary>
public class BasicTestBlockchain : TestBlockchain
{
    public static async Task<BasicTestBlockchain> Create(Action<ContainerBuilder>? configurer = null)
    {
        BasicTestBlockchain chain = new();
        await chain.Build(configurer);
        return chain;
    }

    protected override Task AddBlocksOnStart() => Task.CompletedTask;

    public async Task BuildSomeBlocks(int numOfBlocks)
    {
        var nonce = WorldStateManager.GlobalStateReader.GetNonce(BlockTree.Head!.Header, TestItem.PrivateKeyA.Address);
        for (int i = 0; i < numOfBlocks; i++)
        {
            IReleaseSpec spec = SpecProvider.GetSpec(BlockTree.Head!.Header);

            await AddBlock(Builders.Build.A.Transaction
                .WithTo(TestItem.AddressD)
                .WithNonce(nonce)
                .WithGasLimit(GasCostOf.Transaction)
                .SignedAndResolved(TestItem.PrivateKeyA, spec.IsEip155Enabled).TestObject);
            nonce++;
        }
    }
}

