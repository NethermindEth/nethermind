// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

/// <summary>EIP-8141 in-pool validation-prefix simulation over the node's own wiring.</summary>
/// <remarks>A deploy frame deposits code into the env's <see cref="ICodeCache"/>, and nothing journals a code
/// cache, so the deposit outlives the rollback that discards the prefix. What keeps that off the main
/// processing cache is the registration giving the simulator an env with its own cache; only a simulator
/// resolved from the container exercises it.</remarks>
[TestFixture]
public class FrameTxPrefixSimulatorCodeCacheTests
{
    private static readonly Address Factory = TestItem.AddressB;
    private static readonly byte[] Salt = new byte[32];

    [Test]
    public async Task A_simulated_deploy_frame_does_not_deposit_into_the_main_processing_code_cache()
    {
        // Marked so the assertion cannot be satisfied by a hash the process-wide cache never held anyway.
        byte[] deployedCode = Prepare.EvmCode
            .PushData(0x8141).Op(Instruction.POP)
            .PushData(TxFrame.ApproveExecutionAndPayment).PushData(0).PushData(0).Op(Instruction.APPROVE).Done;
        byte[] initCode = Prepare.EvmCode.ForInitOf(deployedCode).Done;
        Address deployed = ContractAddress.From(Factory, Salt, initCode);
        ValueHash256 depositedHash = Keccak.Compute(deployedCode).ValueHash256;

        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder =>
        {
            builder.AddSingleton<ISpecProvider>(new TestSpecProvider(Eip8141Prototype.Instance));
            builder.AddScoped<IGenesisPostProcessor, IWorldState, ISpecProvider>((worldState, specProvider) =>
                new FunctionalGenesisPostProcessor(_ =>
                {
                    worldState.CreateAccount(Factory, UInt256.Zero);
                    worldState.InsertCode(Factory, Prepare.EvmCode.Create2(initCode, Salt, 0).Done, specProvider.GenesisSpec);
                    worldState.CreateAccount(deployed, 10.Ether);
                    worldState.RecalculateStateRoot();
                }));
        });

        IFrameTxPrefixSimulator simulator = chain.Container.Resolve<IFrameTxPrefixSimulator>();
        ICodeCache mainCache = chain.Container.Resolve<ICodeCache>();

        FrameTxSimulationResult result = simulator.Simulate(DeployTx(deployed), local: true);

        using (Assert.EnterMultipleScope())
        {
            // Acceptance is what proves the create ran: the payer resolves only from the deployed code's APPROVE.
            Assert.That(result.Payer, Is.EqualTo(deployed), result.Reason);
            Assert.That(mainCache.Get(in depositedHash), Is.Null,
                "the simulated deployment reached the cache block processing reads");
        }
    }

    private static Transaction DeployTx(Address deployed) => new()
    {
        Type = TxType.FrameTx,
        ChainId = TestBlockchainIds.ChainId,
        Nonce = 0,
        SenderAddress = deployed,
        Frames =
        [
            new TxFrame(TxFrame.ModeDefault, TxFrame.ApproveScopeNone, Factory, executionGasLimit: 200_000, stateGasLimit: 200_000, UInt256.Zero, default),
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 200_000, UInt256.Zero, default),
        ],
        FrameSignatures = [],
        GasPrice = 1.GWei,
        DecodedMaxFeePerGas = 1.GWei,
    };
}
