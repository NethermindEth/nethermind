// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

/// <summary>EIP-8369 Profile 2 eligibility replayed at an evaluation index over the node's own wiring.</summary>
/// <remarks>The sender approves only while the storage slot it reads is zero, so the access list decides the
/// verdict by writing that slot, or the sender's nonce or balance, at a chosen block access index.</remarks>
[TestFixture]
public class Profile2EligibilityReplayerTests
{
    private static readonly Address Sender = TestItem.AddressD;
    private static readonly IReleaseSpec Spec = Eip8141Prototype.Instance;

    public static IEnumerable<TestCaseData> Cases
    {
        get
        {
            // Block access index 0 is pre-execution; transaction i writes at index i + 1.
            static TestCaseData Case(string name, byte slot, ReadOnlyAccountChanges? changes, int index, bool eligible) =>
                new(slot, changes, index, eligible) { TestName = name };

            ReadOnlyAccountChanges slotWrittenByTx1 = Changes(storage: [new ReadOnlySlotChanges(0, [new StorageChange(2, 1)])]);
            yield return Case("Eligible before the transaction that invalidates it", 0, slotWrittenByTx1, 1, true);
            yield return Case("Ineligible after the transaction that invalidates it", 0, slotWrittenByTx1, 2, false);
            yield return Case("Pre-execution writes precede index zero", 0,
                Changes(storage: [new ReadOnlySlotChanges(0, [new StorageChange(0, 1)])]), 0, false);

            ReadOnlyAccountChanges nonceBumpedByTx0 = Changes(nonces: [new NonceChange(1, 1)]);
            yield return Case("Nonce still matches before the transaction that bumps it", 0, nonceBumpedByTx0, 0, true);
            yield return Case("Nonce mismatch after the transaction that bumps it", 0, nonceBumpedByTx0, 1, false);

            yield return Case("Payer that cannot cover the maximum cost is ineligible", 0,
                Changes(balances: [new BalanceChange(1, UInt256.Zero)]), 1, false);

            yield return Case("Read of the last slot inside the surface is eligible", (byte)(Eip8369Constants.AaVopsSlotCount - 1), null, 0, true);
            yield return Case("Read of the first slot outside the surface is ineligible", (byte)Eip8369Constants.AaVopsSlotCount, null, 0, false);
        }
    }

    [TestCaseSource(nameof(Cases))]
    public async Task Replays_the_validation_prefix_at_the_evaluation_index(byte slot, ReadOnlyAccountChanges? changes, int index, bool eligible)
    {
        using BasicTestBlockchain chain = await CreateChain(slot);
        IProfile2EligibilityReplayer replayer = chain.Container.Resolve<IProfile2EligibilityReplayer>();
        Block block = ChildOfHead(chain, new ReadOnlyBlockAccessList([changes ?? Changes()], 1));

        Assert.That(replayer.IsEligible(block, FrameTx(), index, Spec), Is.EqualTo(eligible));
    }

    /// <summary>With no access list there is no state to reconstruct, and EIP-8369 makes that no excuse.</summary>
    [Test]
    public async Task A_block_without_an_access_list_leaves_the_candidate_enforced()
    {
        using BasicTestBlockchain chain = await CreateChain(slot: (byte)Eip8369Constants.AaVopsSlotCount);
        IProfile2EligibilityReplayer replayer = chain.Container.Resolve<IProfile2EligibilityReplayer>();

        Assert.That(replayer.IsEligible(ChildOfHead(chain, null), FrameTx(), 0, Spec), Is.True);
    }

    private static Task<BasicTestBlockchain> CreateChain(byte slot) => BasicTestBlockchain.Create(builder =>
    {
        builder.AddSingleton<ISpecProvider>(new TestSpecProvider(Spec));
        builder.AddScoped<IGenesisPostProcessor, IWorldState, ISpecProvider>((worldState, specProvider) =>
            new FunctionalGenesisPostProcessor(_ =>
            {
                worldState.CreateAccount(Sender, 10.Ether);
                worldState.InsertCode(Sender, ApproveWhileSlotIsZero(slot), specProvider.GenesisSpec);
                worldState.RecalculateStateRoot();
            }));
    });

    private static Block ChildOfHead(BasicTestBlockchain chain, ReadOnlyBlockAccessList? blockAccessList)
    {
        BlockHeader head = chain.BlockTree.Head!.Header;
        Block block = Build.A.Block
            .WithParent(head)
            .WithTimestamp(head.Timestamp + 12)
            .WithGasLimit(30_000_000)
            .WithTransactions(Build.A.Transaction.TestObject, Build.A.Transaction.WithNonce(1).TestObject)
            .TestObject;
        block.BlockAccessList = blockAccessList;
        return block;
    }

    private static ReadOnlyAccountChanges Changes(
        ReadOnlySlotChanges[]? storage = null, NonceChange[]? nonces = null, BalanceChange[]? balances = null) =>
        new(Sender, storage ?? [], [], balances ?? [], nonces ?? [], []);

    // SLOAD(slot); nonzero jumps to a revert, zero approves execution and payment.
    private static byte[] ApproveWhileSlotIsZero(byte slot) =>
    [
        (byte)Instruction.PUSH1, slot, (byte)Instruction.SLOAD,
        (byte)Instruction.PUSH1, 0x0e, (byte)Instruction.JUMPI,
        (byte)Instruction.PUSH1, (byte)FrameFlags.ApproveExecutionAndPayment,
        (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 0, (byte)Instruction.APPROVE,
        (byte)Instruction.STOP,
        (byte)Instruction.JUMPDEST, (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 0, (byte)Instruction.REVERT,
    ];

    private static Transaction FrameTx() => new()
    {
        Type = TxType.FrameTx,
        ChainId = TestBlockchainIds.ChainId,
        Nonce = 0,
        SenderAddress = Sender,
        Frames = [new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, default)],
        FrameSignatures = [],
        GasPrice = 1.GWei,
        DecodedMaxFeePerGas = 1.GWei,
    };
}
