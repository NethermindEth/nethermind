// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

[Parallelizable(ParallelScope.All)]
public class BlockCachePreWarmerTests
{
    [Test]
    public async Task Blocks_with_prewarmer_produce_same_state_root_as_without()
    {
        const int blockCount = 10;

        // Build without prewarmer
        using PreWarmerTestBlockchain noPrewarmerChain = await PreWarmerTestBlockchain.Create(enablePrewarmer: false);
        Hash256[] expectedRoots = await BuildSimpleBlocks(noPrewarmerChain, blockCount);

        // Build identical blocks with prewarmer
        using PreWarmerTestBlockchain prewarmerChain = await PreWarmerTestBlockchain.Create(enablePrewarmer: true);
        Hash256[] actualRoots = await BuildSimpleBlocks(prewarmerChain, blockCount);

        for (int i = 0; i < blockCount; i++)
        {
            actualRoots[i].Should().Be(expectedRoots[i], $"State root for block {i} should match");
        }
    }

    [Test]
    public async Task Many_blocks_with_prewarmer_maintain_correctness()
    {
        const int blockCount = 30;
        const int txPerBlock = 8;

        Address[] recipients = CreateRecipients(64);

        // Build without prewarmer
        using PreWarmerTestBlockchain refChain = await PreWarmerTestBlockchain.Create(enablePrewarmer: false);
        Hash256[] expectedRoots = await BuildBlocksWithRecipients(refChain, blockCount, txPerBlock, recipients);

        // Build with prewarmer
        using PreWarmerTestBlockchain testChain = await PreWarmerTestBlockchain.Create(enablePrewarmer: true);
        Hash256[] actualRoots = await BuildBlocksWithRecipients(testChain, blockCount, txPerBlock, recipients);

        for (int i = 0; i < blockCount; i++)
        {
            actualRoots[i].Should().Be(expectedRoots[i], $"State root for block {i} should match");
        }
    }

    [Test]
    public async Task Multiple_senders_with_prewarmer_maintain_correctness()
    {
        const int blockCount = 10;

        // Build without prewarmer
        using PreWarmerTestBlockchain refChain = await PreWarmerTestBlockchain.Create(enablePrewarmer: false);
        Hash256[] expectedRoots = await BuildMultiSenderBlocks(refChain, blockCount);

        // Build with prewarmer
        using PreWarmerTestBlockchain testChain = await PreWarmerTestBlockchain.Create(enablePrewarmer: true);
        Hash256[] actualRoots = await BuildMultiSenderBlocks(testChain, blockCount);

        for (int i = 0; i < blockCount; i++)
        {
            actualRoots[i].Should().Be(expectedRoots[i], $"State root for block {i} should match");
        }
    }

    [Test]
    public async Task Mixed_empty_and_nonempty_blocks_with_prewarmer()
    {
        const int blockCount = 20;

        // Build without prewarmer
        using PreWarmerTestBlockchain refChain = await PreWarmerTestBlockchain.Create(enablePrewarmer: false);
        Hash256[] expectedRoots = await BuildMixedBlocks(refChain, blockCount);

        // Build with prewarmer
        using PreWarmerTestBlockchain testChain = await PreWarmerTestBlockchain.Create(enablePrewarmer: true);
        Hash256[] actualRoots = await BuildMixedBlocks(testChain, blockCount);

        for (int i = 0; i < blockCount; i++)
        {
            actualRoots[i].Should().Be(expectedRoots[i], $"State root for block {i} should match");
        }
    }

    [Test]
    public async Task Prewarmer_gas_accounting_matches_baseline()
    {
        const int blockCount = 15;

        // Build without prewarmer
        using PreWarmerTestBlockchain refChain = await PreWarmerTestBlockchain.Create(enablePrewarmer: false);
        UInt256 refNonce = refChain.WorldStateManager.GlobalStateReader.GetNonce(
            refChain.BlockTree.Head.Header, TestItem.PrivateKeyA.Address);

        long expectedGas = 0;
        for (int i = 0; i < blockCount; i++)
        {
            IReleaseSpec spec = refChain.SpecProvider.GetSpec(refChain.BlockTree.Head.Header);
            Transaction tx = Build.A.Transaction
                .WithTo(TestItem.AddressB)
                .WithValue(UInt256.One)
                .WithGasLimit(GasCostOf.Transaction)
                .WithNonce(refNonce++)
                .SignedAndResolved(TestItem.PrivateKeyA, spec.IsEip155Enabled)
                .TestObject;
            Block block = await refChain.AddBlock(tx);
            expectedGas += block.Header.GasUsed;
        }

        // Build with prewarmer
        using PreWarmerTestBlockchain testChain = await PreWarmerTestBlockchain.Create(enablePrewarmer: true);
        UInt256 testNonce = testChain.WorldStateManager.GlobalStateReader.GetNonce(
            testChain.BlockTree.Head.Header, TestItem.PrivateKeyA.Address);

        long actualGas = 0;
        for (int i = 0; i < blockCount; i++)
        {
            IReleaseSpec spec = testChain.SpecProvider.GetSpec(testChain.BlockTree.Head.Header);
            Transaction tx = Build.A.Transaction
                .WithTo(TestItem.AddressB)
                .WithValue(UInt256.One)
                .WithGasLimit(GasCostOf.Transaction)
                .WithNonce(testNonce++)
                .SignedAndResolved(TestItem.PrivateKeyA, spec.IsEip155Enabled)
                .TestObject;
            Block block = await testChain.AddBlock(tx);
            actualGas += block.Header.GasUsed;
        }

        actualGas.Should().Be(expectedGas);
        testChain.BlockTree.Head.Header.StateRoot.Should().Be(refChain.BlockTree.Head.Header.StateRoot);
    }

    private static async Task<Hash256[]> BuildSimpleBlocks(PreWarmerTestBlockchain chain, int blockCount)
    {
        Hash256[] roots = new Hash256[blockCount];
        UInt256 nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(
            chain.BlockTree.Head.Header, TestItem.PrivateKeyA.Address);

        for (int i = 0; i < blockCount; i++)
        {
            IReleaseSpec spec = chain.SpecProvider.GetSpec(chain.BlockTree.Head.Header);
            Transaction tx = Build.A.Transaction
                .WithTo(TestItem.AddressB)
                .WithValue(UInt256.One)
                .WithGasLimit(GasCostOf.Transaction)
                .WithNonce(nonce++)
                .SignedAndResolved(TestItem.PrivateKeyA, spec.IsEip155Enabled)
                .TestObject;
            Block block = await chain.AddBlock(tx);
            roots[i] = block.Header.StateRoot;
        }

        return roots;
    }

    private static async Task<Hash256[]> BuildBlocksWithRecipients(
        PreWarmerTestBlockchain chain, int blockCount, int txPerBlock, Address[] recipients)
    {
        Hash256[] roots = new Hash256[blockCount];
        UInt256 nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(
            chain.BlockTree.Head.Header, TestItem.PrivateKeyA.Address);

        for (int b = 0; b < blockCount; b++)
        {
            IReleaseSpec spec = chain.SpecProvider.GetSpec(chain.BlockTree.Head.Header);
            Transaction[] txs = new Transaction[txPerBlock];
            for (int t = 0; t < txPerBlock; t++)
            {
                Address recipient = recipients[(b * txPerBlock + t) % recipients.Length];
                txs[t] = Build.A.Transaction
                    .WithTo(recipient)
                    .WithValue(UInt256.One)
                    .WithGasLimit(GasCostOf.Transaction)
                    .WithNonce(nonce++)
                    .SignedAndResolved(TestItem.PrivateKeyA, spec.IsEip155Enabled)
                    .TestObject;
            }

            Block block = await chain.AddBlock(txs);
            roots[b] = block.Header.StateRoot;
        }

        return roots;
    }

    private static async Task<Hash256[]> BuildMultiSenderBlocks(PreWarmerTestBlockchain chain, int blockCount)
    {
        Hash256[] roots = new Hash256[blockCount];
        UInt256 nonceA = chain.WorldStateManager.GlobalStateReader.GetNonce(
            chain.BlockTree.Head.Header, TestItem.PrivateKeyA.Address);
        UInt256 nonceB = chain.WorldStateManager.GlobalStateReader.GetNonce(
            chain.BlockTree.Head.Header, TestItem.PrivateKeyB.Address);

        for (int b = 0; b < blockCount; b++)
        {
            IReleaseSpec spec = chain.SpecProvider.GetSpec(chain.BlockTree.Head.Header);

            Transaction txA = Build.A.Transaction
                .WithTo(TestItem.AddressC)
                .WithValue(UInt256.One)
                .WithGasLimit(GasCostOf.Transaction)
                .WithNonce(nonceA++)
                .SignedAndResolved(TestItem.PrivateKeyA, spec.IsEip155Enabled)
                .TestObject;

            Transaction txB = Build.A.Transaction
                .WithTo(TestItem.AddressD)
                .WithValue(UInt256.One)
                .WithGasLimit(GasCostOf.Transaction)
                .WithNonce(nonceB++)
                .SignedAndResolved(TestItem.PrivateKeyB, spec.IsEip155Enabled)
                .TestObject;

            Block block = await chain.AddBlock(txA, txB);
            roots[b] = block.Header.StateRoot;
        }

        return roots;
    }

    private static async Task<Hash256[]> BuildMixedBlocks(PreWarmerTestBlockchain chain, int blockCount)
    {
        Hash256[] roots = new Hash256[blockCount];
        UInt256 nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(
            chain.BlockTree.Head.Header, TestItem.PrivateKeyA.Address);

        for (int b = 0; b < blockCount; b++)
        {
            Block block;
            if (b % 3 == 0)
            {
                block = await chain.AddBlock();
            }
            else
            {
                IReleaseSpec spec = chain.SpecProvider.GetSpec(chain.BlockTree.Head.Header);
                Transaction tx = Build.A.Transaction
                    .WithTo(TestItem.AddressB)
                    .WithValue(UInt256.One)
                    .WithGasLimit(GasCostOf.Transaction)
                    .WithNonce(nonce++)
                    .SignedAndResolved(TestItem.PrivateKeyA, spec.IsEip155Enabled)
                    .TestObject;
                block = await chain.AddBlock(tx);
            }

            roots[b] = block.Header.StateRoot;
        }

        return roots;
    }

    private static Address[] CreateRecipients(int count)
    {
        Address[] recipients = new Address[count];
        System.Random random = new(17);
        byte[] buffer = new byte[Address.Size];
        for (int i = 0; i < count; i++)
        {
            random.NextBytes(buffer);
            recipients[i] = new Address((byte[])buffer.Clone());
        }

        return recipients;
    }

    private sealed class PreWarmerTestBlockchain(bool enablePrewarmer) : BasicTestBlockchain
    {
        private readonly bool _enablePrewarmer = enablePrewarmer;

        public static async Task<PreWarmerTestBlockchain> Create(bool enablePrewarmer)
        {
            PreWarmerTestBlockchain chain = new(enablePrewarmer)
            {
                TestTimeout = 120_000
            };
            await chain.Build();
            return chain;
        }

        protected override IEnumerable<IConfig> CreateConfigs()
        {
            BlocksConfig blocksConfig = new()
            {
                MinGasPrice = 0,
                PreWarmStateOnBlockProcessing = _enablePrewarmer,
                CachePrecompilesOnBlockProcessing = true,
                PreWarmStateConcurrency = 0
            };

            return [blocksConfig];
        }
    }
}
