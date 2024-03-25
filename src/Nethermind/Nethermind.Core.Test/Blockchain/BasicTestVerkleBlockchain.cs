// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Test.Builders;

namespace Nethermind.Core.Test.Blockchain;

public class BasicTestVerkleBlockchain : TestVerkleBlockchain
{
    public static async Task<BasicTestVerkleBlockchain> Create()
    {
        var chain = new BasicTestVerkleBlockchain();
        await chain.Build();
        return chain;
    }

    protected override Task AddBlocksOnStart() => Task.CompletedTask;

    public async Task BuildSomeBlocks(int numOfBlocks)
    {
        for (int i = 0; i < numOfBlocks; i++)
        {
            await AddBlock(Core.Test.Builders.Build.A.Transaction.WithTo(TestItem.AddressD)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject);
        }
    }
}
