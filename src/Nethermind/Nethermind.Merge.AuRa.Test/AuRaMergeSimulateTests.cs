// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Evm.State;
using Nethermind.Facade;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.Int256;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Merge.AuRa.Test;

public class AuRaMergeSimulateTests : BaseEngineModuleTests
{
    private const ulong ContractGasLimit = 12_500_000;
    private static readonly Address GasLimitContract = new("0x4000000000000000000000000000000000000001");

    // Answers any call, including blockGasLimit(), with ContractGasLimit as a uint256.
    private static readonly byte[] GasLimitContractCode = Bytes.FromHexString("0x62bebc2060005260206000f3");

    // Regression: on Chiado the block gas limit contract still returns 12.5M while post-merge blocks carry 17M.
    // A node that synced past the merge never records the terminal block, so MergeBlockNumber stays unknown and
    // eth_simulateV1 built pre-merge headers, which AuRa validated against the contract and rejected.
    [Test]
    public async Task Simulate_on_post_merge_block_ignores_block_gas_limit_contract()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(London.Instance, configurer: builder => builder
            .WithGenesisPostProcessor((_, state) =>
            {
                state.CreateAccount(GasLimitContract, UInt256.Zero);
                state.InsertCode(GasLimitContract, GasLimitContractCode, London.Instance);
            }));
        await ProduceBranchV1(chain.EngineRpcModule, chain, 1, CreateParentBlockRequestOnHead(chain.BlockTree), setHead: true);

        BlockHeader head = chain.BlockTree.Head!.Header;
        Assume.That(chain.SpecProvider.MergeBlockNumber, Is.Null);
        Assume.That(head.Difficulty, Is.EqualTo(UInt256.Zero));
        Assume.That(head.GasLimit, Is.Not.EqualTo(ContractGasLimit));

        Transaction transfer = Build.A.Transaction
            .WithSenderAddress(new Address("0x0000000000000000000000000000000000000001"))
            .To(new Address("0x0000000000000000000000000000000000000002"))
            .WithValue(UInt256.Zero)
            .WithGasPrice(0)
            .TestObject;

        SimulatePayload<TransactionWithSourceDetails> payload = new()
        {
            BlockStateCalls =
            [
                new BlockStateCall<TransactionWithSourceDetails>
                {
                    Calls = [new TransactionWithSourceDetails { Transaction = transfer }]
                }
            ]
        };

        SimulateOutput<SimulateCallResult> result = chain.Container.Resolve<IBlockchainBridge>().Simulate(
            head, payload, new SimulateBlockMutatorTracerFactory(), 10_000_000, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Items, Has.Count.EqualTo(1));
            Assert.That(result.Items[0].GasLimit, Is.EqualTo(head.GasLimit));
        }
    }

    protected override MergeTestBlockchain CreateBaseBlockchain(IMergeConfig? mergeConfig = null) =>
        new GasLimitContractAuRaMergeTestBlockchain();

    private class GasLimitContractAuRaMergeTestBlockchain : AuRaMergeEngineModuleTests.MergeAuRaTestBlockchain
    {
        protected override ChainSpec CreateChainSpec()
        {
            ChainSpec chainSpec = base.CreateChainSpec();
            chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<AuRaChainSpecEngineParameters>()
                .BlockGasLimitContractTransitions = new Dictionary<ulong, Address> { { 0, GasLimitContract } };
            return chainSpec;
        }
    }
}
