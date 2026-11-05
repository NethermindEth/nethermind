// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Facade;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using ResultType = Nethermind.Facade.Proxy.Models.Simulate.ResultType;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>
/// PREVRANDAO on a chain that merged by terminal total difficulty without pinning the terminal block, like Sepolia and
/// Mainnet: a node that synced past the merge never learns the merge block number.
/// </summary>
public class SimulatePrevRandaoTests : BaseEngineModuleTests
{
    private static readonly Address Contract = new("0xb2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2");
    private static readonly Address Sender = new("0xa1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1");
    private static readonly Hash256 PrevRandaoOverride = new("0x1111111111111111111111111111111111111111111111111111111111111111");
    private static readonly Hash256 HeadRandom = TestItem.KeccakA;

    // PREVRANDAO PUSH1 0 MSTORE PUSH1 32 PUSH1 0 RETURN
    private static readonly byte[] PrevRandaoCode = Bytes.FromHexString("0x4460005260206000f3");

    [TestCase(true, TestName = "Simulate_prevrandao_with_override_on_a_chain_merged_by_ttd")]
    [TestCase(false, TestName = "Simulate_prevrandao_without_override_on_a_chain_merged_by_ttd")]
    public async Task Simulate_prevrandao_reads_the_simulated_mix_hash(bool overridden)
    {
        using MergeTestBlockchain chain = await CreateChain();
        BlockHeader head = chain.BlockTree.Head!.Header;

        SimulatePayload<TransactionWithSourceDetails> payload = new()
        {
            BlockStateCalls =
            [
                new BlockStateCall<TransactionWithSourceDetails>
                {
                    BlockOverrides = overridden ? new BlockOverride { PrevRandao = PrevRandaoOverride } : null,
                    Calls = [new TransactionWithSourceDetails { Transaction = CreateCall() }]
                }
            ]
        };

        SimulateOutput<SimulateCallResult> result = chain.Container.Resolve<IBlockchainBridge>().Simulate(
            head, payload, new SimulateBlockMutatorTracerFactory(), 10_000_000, CancellationToken.None);

        Assert.That(result.Error, Is.Null);
        SimulateCallResult call = result.Items.Single().Calls.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(call.Status, Is.EqualTo((ulong)ResultType.Success));
            Assert.That(new Hash256(call.ReturnData!), Is.EqualTo(overridden ? PrevRandaoOverride : Hash256.Zero));
        }
    }

    [TestCase(true, TestName = "Call_prevrandao_with_override_on_a_chain_merged_by_ttd")]
    [TestCase(false, TestName = "Call_prevrandao_without_override_on_a_chain_merged_by_ttd")]
    public async Task Call_prevrandao_reads_the_block_mix_hash(bool overridden)
    {
        using MergeTestBlockchain chain = await CreateChain();
        BlockHeader head = chain.BlockTree.Head!.Header;

        CallOutput output = chain.Container.Resolve<IBlockchainBridge>().Call(head, CreateCall(),
            blockOverride: overridden ? new BlockOverride { PrevRandao = PrevRandaoOverride } : null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(output.Error, Is.Null);
            Assert.That(new Hash256(output.OutputData), Is.EqualTo(overridden ? PrevRandaoOverride : HeadRandom));
        }
    }

    private async Task<MergeTestBlockchain> CreateChain()
    {
        MergeTestBlockchain chain = await CreateBlockchain(Paris.Instance, configurer: builder => builder
            .WithGenesisPostProcessor((_, state) =>
            {
                state.CreateAccount(Contract, UInt256.Zero);
                state.InsertCode(Contract, PrevRandaoCode, Paris.Instance);
            }));
        await ProduceBranchV1(chain.EngineRpcModule, chain, 1, CreateParentBlockRequestOnHead(chain.BlockTree), setHead: true, random: HeadRandom);

        BlockHeader head = chain.BlockTree.Head!.Header;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.SpecProvider.TerminalTotalDifficulty, Is.Not.Null);
            Assert.That(chain.SpecProvider.MergeBlockNumber, Is.Null);
            Assert.That(head.Difficulty, Is.EqualTo(UInt256.Zero));
            Assert.That(head.MixHash, Is.EqualTo(HeadRandom));
        }
        return chain;
    }

    private static Transaction CreateCall() => Build.A.Transaction
        .WithSenderAddress(Sender)
        .To(Contract)
        .WithValue(UInt256.Zero)
        .WithGasLimit(100_000)
        .WithGasPrice(0)
        .TestObject;
}
