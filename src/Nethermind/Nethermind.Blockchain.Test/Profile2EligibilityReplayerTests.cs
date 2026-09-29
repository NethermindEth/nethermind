// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
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
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

/// <summary>EIP-8369 Profile 2 eligibility replayed at an evaluation index over the node's own wiring.</summary>
/// <remarks>The sender approves only while the storage slot it reads is zero, so the access list decides the
/// verdict by writing that slot, or the sender's nonce or balance, at a chosen block access index.</remarks>
[TestFixture]
public class Profile2EligibilityReplayerTests
{
    private static readonly Address Sender = TestItem.AddressD;
    private static readonly IReleaseSpec Spec = new OverridableReleaseSpec(Eip8141Prototype.Instance) { IsEip8272Enabled = true };
    private const ulong BlockSlot = 10_000;

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

        Assert.That(IsEligible(replayer, block, FrameTx(), index), Is.EqualTo(eligible));
    }

    /// <summary>EIP-8369 Profile 2 bounds the prefix by <c>MAX_VERIFY_GAS_PER_TX</c>, not EIP-8141's mempool
    /// <c>MAX_VERIFY_GAS</c>, so a prefix spending between the two is eligible.</summary>
    /// <remarks>The frame declares 500,000; the wide expansion alone runs past <c>MAX_VERIFY_GAS</c>, which the
    /// replayer under the mempool bound rejects as exceeding it.</remarks>
    [TestCase(64UL, TestName = "Prefix well under MAX_VERIFY_GAS is eligible")]
    [TestCase(12_000UL, TestName = "Prefix over MAX_VERIFY_GAS but under MAX_VERIFY_GAS_PER_TX is eligible")]
    public async Task Prefix_is_bounded_by_the_profile_2_cap(ulong memoryWords)
    {
        using BasicTestBlockchain chain = await CreateChain(ExpandMemoryThenApprove(memoryWords));
        IProfile2EligibilityReplayer replayer = chain.Container.Resolve<IProfile2EligibilityReplayer>();

        Transaction tx = FrameTx(verifyGas: 500_000);
        Assert.That(IsEligible(replayer, ChildOfHead(chain, new ReadOnlyBlockAccessList([Changes()], 1)), tx, 0), Is.True);
    }

    /// <summary>EIP-8369 § Attesters takes <c>current_slot</c> from the block itself, so a reference exactly
    /// <c>RECENT_ROOT_USABLE_WINDOW</c> slots back is still usable, and one at the block's own slot is not.</summary>
    [TestCase(BlockSlot - Eip8272Constants.RecentRootUsableWindow, true, TestName = "Recent root at the oldest usable slot is eligible")]
    [TestCase(BlockSlot - 1, true, TestName = "Recent root one slot back is eligible")]
    [TestCase(BlockSlot, false, TestName = "Recent root at the block's own slot is ineligible")]
    public async Task Recent_roots_are_anchored_at_the_blocks_own_slot(ulong referenceSlot, bool eligible)
    {
        ValueHash256 sourceId = Keccak.Compute("source").ValueHash256;
        ValueHash256 root = Keccak.Compute("root").ValueHash256;
        using BasicTestBlockchain chain = await CreateChain(ApproveWhileSlotIsZero(0), worldState =>
        {
            // A storage-only account is empty under EIP-161, so the predeploy's store needs a nonce to survive genesis.
            worldState.CreateAccountIfNotExists(Eip8272Constants.RecentRootAddress, UInt256.Zero, 1);
            worldState.Set(RecentRootStore.ReferenceCell(sourceId, referenceSlot), RecentRootStore.EntryHash(sourceId, referenceSlot, root).ToUInt256());
        });
        IProfile2EligibilityReplayer replayer = chain.Container.Resolve<IProfile2EligibilityReplayer>();

        Transaction tx = FrameTx();
        tx.RecentRootReferences = [new RecentRootReference(sourceId, referenceSlot, root)];
        Assert.That(IsEligible(replayer, ChildOfHead(chain, new ReadOnlyBlockAccessList([Changes()], 1)), tx, 0), Is.EqualTo(eligible));
    }

    /// <summary>With no access list there is no state to reconstruct, and EIP-8369 makes that no excuse.</summary>
    [Test]
    public async Task A_block_without_an_access_list_leaves_the_candidate_enforced()
    {
        using BasicTestBlockchain chain = await CreateChain(slot: (byte)Eip8369Constants.AaVopsSlotCount);
        IProfile2EligibilityReplayer replayer = chain.Container.Resolve<IProfile2EligibilityReplayer>();

        Assert.That(IsEligible(replayer, ChildOfHead(chain, null), FrameTx(), 0), Is.True);
    }

    /// <summary>Several candidates at several indices, in no particular order, reconstruct the block's state
    /// once and still see each index's own state.</summary>
    [Test]
    public async Task A_batch_reconstructs_the_state_once_and_judges_each_index_on_its_own_state()
    {
        using BasicTestBlockchain chain = await CreateChain(slot: 0);
        Profile2EligibilityReplayer replayer = (Profile2EligibilityReplayer)chain.Container.Resolve<IProfile2EligibilityReplayer>();
        Block block = ChildOfHead(chain, new ReadOnlyBlockAccessList(
            [Changes(storage: [new ReadOnlySlotChanges(0, [new StorageChange(2, 1), new StorageChange(3, 0)])])], 1));
        long before = replayer.StateReconstructions;

        bool[] eligible = replayer.AreEligible(block,
            [(FrameTx(), 2), (FrameTx(), 0), (FrameTx(), 1), (FrameTx(), 0), (FrameTx(), 2), (FrameTx(), 1), (FrameTx(), 3)], Spec);

        using (Assert.EnterMultipleScope())
        {
            // Slot 0 is set by transaction 1 and cleared by transaction 2, so only index 2 sees it set.
            Assert.That(eligible, Is.EqualTo(new[] { false, true, true, true, false, true, true }));
            Assert.That(replayer.StateReconstructions - before, Is.EqualTo(1));
        }
    }

    private static bool IsEligible(IProfile2EligibilityReplayer replayer, Block block, Transaction tx, int index) =>
        replayer.AreEligible(block, [(tx, index)], Spec)[0];

    private static Task<BasicTestBlockchain> CreateChain(byte slot) => CreateChain(ApproveWhileSlotIsZero(slot));

    private static Task<BasicTestBlockchain> CreateChain(byte[] senderCode, Action<IWorldState>? genesis = null) => BasicTestBlockchain.Create(builder =>
    {
        builder.AddSingleton<ISpecProvider>(new TestSpecProvider(Spec));
        builder.AddScoped<IGenesisPostProcessor, IWorldState, ISpecProvider>((worldState, specProvider) =>
            new FunctionalGenesisPostProcessor(_ =>
            {
                worldState.CreateAccount(Sender, 10.Ether);
                worldState.InsertCode(Sender, senderCode, specProvider.GenesisSpec);
                genesis?.Invoke(worldState);
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
            .WithSlotNumber(BlockSlot)
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

    // Expands memory to the given number of words with one MLOAD, then approves execution and payment.
    private static byte[] ExpandMemoryThenApprove(ulong words)
    {
        ulong offset = (words - 1) * 32;
        return
        [
            (byte)Instruction.PUSH3, (byte)(offset >> 16), (byte)(offset >> 8), (byte)offset,
            (byte)Instruction.MLOAD, (byte)Instruction.POP,
            (byte)Instruction.PUSH1, (byte)FrameFlags.ApproveExecutionAndPayment,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 0, (byte)Instruction.APPROVE,
            (byte)Instruction.STOP,
        ];
    }

    private static Transaction FrameTx(ulong verifyGas = 100_000) => new()
    {
        Type = TxType.FrameTx,
        ChainId = TestBlockchainIds.ChainId,
        Nonce = 0,
        SenderAddress = Sender,
        Frames = [new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, gasLimit: verifyGas, UInt256.Zero, default)],
        FrameSignatures = [],
        GasPrice = 1.GWei,
        DecodedMaxFeePerGas = 1.GWei,
    };
}
