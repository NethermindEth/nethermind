// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Test.Builders;

namespace Nethermind.Core.Test.Blockchain;

/// <summary>
/// For when you need blocks with state roots modified.
/// </summary>
public class BasicTestBlockchain : TestBlockchain
{
    public static async Task<BasicTestBlockchain> Create(long slotTime = 1)
    {
        BasicTestBlockchain chain = new();
        await chain.Build(slotTime: slotTime);
        return chain;
    }

    protected override Task AddBlocksOnStart() => Task.CompletedTask;

    public async Task BuildSomeBlocks(int numOfBlocks)
    {
        for (int i = 0; i < numOfBlocks; i++)
        {
            await AddBlock(Builders.Build.A.Transaction.WithTo(TestItem.AddressD)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject);
        }
    }
}

