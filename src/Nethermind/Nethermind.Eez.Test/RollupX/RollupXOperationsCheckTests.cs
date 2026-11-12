// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public class RollupXOperationsCheckTests
{
    [Test]
    public void Verify_OrdinaryPrefixAndNativeSuffix_AcceptsOnlyPublishedPrefix()
    {
        Transaction ordinary = User(1);
        Transaction native = Build.A.Transaction.WithType(EezConstants.SystemTxType).TestObject;
        EezStatelessBlockResult terminal = Result([ordinary, native], [0, 1]);

        Assert.That(() => RollupXOperationsCheck.Verify(Operations(ordinary), [terminal]), Throws.Nothing,
            "the operations carry the ordinary prefix while the native suffix is derived separately");
    }

    [Test]
    public void Verify_SameCountDifferentTransaction_RejectsBytes([Values(TxType.Legacy, TxType.EIP1559)] TxType type,
        [Values] bool terminal, [Values(0, 1)] int changedIndex)
    {
        Transaction[] actual = [User(1, type), User(2, type)];
        Transaction[] published = [User(1, type), User(2, type)];
        published[changedIndex] = User(3, type);
        EezStatelessBlockResult block = Result(actual, terminal ? [1] : []);
        EezStatelessBlockResult[] window = terminal ? [block] : [block, Result([], [-1])];
        OperationsRun[] runs = terminal ? [Run(published)] : [Run(published), Run()];
        OperationsPayload operations = OperationsPayload.Decode(OperationsPayload.Encode(runs));

        Assert.That(() => RollupXOperationsCheck.Verify(operations, window), Throws.TypeOf<EezSettlementException>(),
            "every ordinary transaction in every block must match its canonical EIP-2718 bytes");
    }

    [TestCase(TxType.Legacy, TestName = "Verify_MatchingLegacyTransaction_AcceptsCanonicalBytes")]
    [TestCase(TxType.EIP1559, TestName = "Verify_MatchingTypedTransaction_AcceptsCanonicalBytes")]
    public void Verify_MatchingTransaction_AcceptsBytes(TxType type)
    {
        Transaction transaction = User(1, type);

        Assert.That(() => RollupXOperationsCheck.Verify(Operations(transaction), [Result([transaction], [0])]), Throws.Nothing,
            "ordinary transactions use canonical unwrapped EIP-2718 encoding");
    }

    [Test]
    public void Verify_MissingOrdinaryTransaction_RejectsCount([Values] bool terminal)
    {
        EezStatelessBlockResult block = Result([User(1)], terminal ? [0] : []);
        EezStatelessBlockResult[] window = terminal ? [block] : [block, Result([], [-1])];

        Assert.That(() => RollupXOperationsCheck.Verify(EmptyOperations(terminal ? 1u : 2u), window), Throws.TypeOf<EezSettlementException>(),
            "ordinary transactions cannot be omitted from any published block");
    }

    [Test]
    public void Verify_DifferentHeaderInputs_RejectsMetadata([Values] bool beneficiary, [Values] bool terminal)
    {
        OperationsRun changed = new(1, beneficiary ? TestItem.AddressB : TestItem.AddressA,
            beneficiary ? new byte[] { 0x42 } : new byte[] { 0x43 }, []);
        OperationsRun[] runs = terminal ? [changed] : [changed, Run()];
        EezStatelessBlockResult[] window = terminal ? [Result([], [-1])] : [Result([], []), Result([], [-1])];
        OperationsPayload operations = OperationsPayload.Decode(OperationsPayload.Encode(runs));

        Assert.That(() => RollupXOperationsCheck.Verify(operations, window), Throws.TypeOf<EezSettlementException>(),
            "DA must publish the beneficiary and extra data used by every executed block");
    }

    [Test]
    public void Verify_IntermediateNativeTransaction_RejectsPrivilegedExecution()
    {
        Transaction native = Build.A.Transaction.WithType(EezConstants.SystemTxType).TestObject;
        EezStatelessBlockResult intermediate = Result([native], []);

        Assert.That(() => RollupXOperationsCheck.Verify(EmptyOperations(2), [intermediate, Result([], [-1])]),
            Throws.TypeOf<EezSettlementException>().With.Message.Contains("system transaction"), "native effects belong only to the terminal block");
    }

    [TestCase(false, TestName = "Verify_IntermediateReservedSender_RejectsPrivilegedIdentity")]
    [TestCase(true, TestName = "Verify_OrdinaryPrefixReservedSender_RejectsPrivilegedIdentity")]
    public void Verify_OrdinaryTransactionHasReservedSender_RejectsIdentity(bool terminal)
    {
        Transaction transaction = User(1);
        transaction.SenderAddress = EezConstants.SystemAddress;
        EezStatelessBlockResult block = Result([transaction], terminal ? [0] : []);
        EezStatelessBlockResult[] window = terminal ? [block] : [block, Result([], [-1])];
        OperationsRun[] runs = terminal ? [Run(transaction)] : [Run(transaction), new(1, TestItem.AddressA, new byte[] { 0x42 }, [])];

        Assert.That(() => RollupXOperationsCheck.Verify(OperationsPayload.Decode(OperationsPayload.Encode(runs)), window),
            Throws.TypeOf<EezSettlementException>(), "a signed ordinary envelope cannot hide the reserved system identity");
    }

    [TestCase(true, false, TestName = "Verify_IntermediateMalformedOutboundEvent_RejectsCandidate")]
    [TestCase(true, true, TestName = "Verify_IntermediateCanonicalOutboundEvent_RejectsCandidate")]
    [TestCase(false, false, TestName = "Verify_IntermediateOtherEmitter_IgnoresUnrelatedLog")]
    public void Verify_IntermediateSignatureMatchedLog_BindsManager(bool rollupXEmitter, bool canonical)
    {
        Transaction transaction = User(1);
        EezStatelessBlockResult block = Result([transaction], []);
        byte[] data = canonical ? new byte[160] : [];
        if (canonical) data[63] = 128;
        Hash256[] topics = canonical ? [OutboundEvent.Signature, Keccak.Zero, Keccak.Zero] : [OutboundEvent.Signature];
        block.Receipts[0].Logs = [new LogEntry(rollupXEmitter ? EezConstants.RollupXEezl2Address : EezConstants.Eezl2Address, data, topics)];
        if (canonical)
            Assert.That(OutboundEvent.Observe(block.Receipts, EezConstants.RollupXEezl2Address)[0].IsCanonical, Is.True,
                "precondition: this case contains a fully canonical outbound event");
        OperationsPayload operations = OperationsPayload.Decode(OperationsPayload.Encode([Run(transaction), Run()]));

        if (rollupXEmitter)
            Assert.That(() => RollupXOperationsCheck.Verify(operations, [block, Result([], [-1])]),
                Throws.TypeOf<EezSettlementException>().With.Message.Contains("outbound event"), "all signature-matched logs retain their effect position");
        else
            Assert.That(() => RollupXOperationsCheck.Verify(operations, [block, Result([], [-1])]), Throws.Nothing,
                "another emitter cannot create a RollupX effect");
    }

    [Test]
    public void Verify_InvalidTerminalCheckpoints_RejectsBackendOutput()
    {
        EezSettlementException failure = Assert.Throws<EezSettlementException>(() =>
            RollupXOperationsCheck.Verify(Operations(), [Result([], [])]))!;

        Assert.That(failure.Failure, Is.EqualTo(EezSettlementFailure.InternalInvariant), "execution must supply the ordinary-prefix checkpoint");
    }

    [Test]
    public void Verify_HugeDeclaredSpan_RejectsBeforeExpandingRuns() =>
        Assert.That(() => RollupXOperationsCheck.Verify(EmptyOperations(uint.MaxValue), [Result([], [-1])]),
            Throws.TypeOf<EezSettlementException>(), "a compact hostile block count cannot cause billions of iterations");

    [Test]
    public void Verify_EmptyWindow_RejectsOperations() =>
        Assert.That(() => RollupXOperationsCheck.Verify(Operations(), []), Throws.TypeOf<EezSettlementException>(),
            "settlement requires an executed terminal block");

    [Test]
    public void Verify_AlreadyCancelled_StopsBeforeWork() =>
        Assert.That(() => RollupXOperationsCheck.Verify(Operations(), [Result([], [-1])], new CancellationToken(true)),
            Throws.TypeOf<OperationCanceledException>(), "cancelled settlement does not start validating operations");

    private static Transaction User(ulong nonce, TxType type = TxType.Legacy) =>
        Build.A.Transaction.WithNonce(nonce).WithType(type).WithChainId(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;

    private static OperationsRun Run(params Transaction[] transactions)
    {
        ReadOnlyMemory<byte>[] encoded = new ReadOnlyMemory<byte>[transactions.Length];
        for (int i = 0; i < transactions.Length; i++)
        {
            Assert.That(transactions[i].Signature, Is.Not.Null, "precondition: ordinary DA inputs contain signed transaction envelopes");
            encoded[i] = TxDecoder.Instance.Encode(transactions[i], RlpBehaviors.SkipTypedWrapping).Bytes;
        }
        return new OperationsRun(1, TestItem.AddressA, new byte[] { 0x42 }, encoded);
    }

    private static OperationsPayload Operations(params Transaction[] transactions) =>
        OperationsPayload.Decode(OperationsPayload.Encode([Run(transactions)]));

    private static OperationsPayload EmptyOperations(uint blocks) => OperationsPayload.Decode(OperationsPayload.Encode(
        [new OperationsRun(blocks, TestItem.AddressA, new byte[] { 0x42 }, [])]));

    private static EezStatelessBlockResult Result(Transaction[] transactions, int[] positions)
    {
        Block block = Build.A.Block.WithBeneficiary(TestItem.AddressA).WithExtraData([0x42]).WithTransactions(transactions).TestObject;
        TxReceipt[] receipts = new TxReceipt[transactions.Length];
        for (int i = 0; i < receipts.Length; i++) receipts[i] = new TxReceipt();
        EezTransactionCheckpoint[] checkpoints = new EezTransactionCheckpoint[positions.Length];
        for (int i = 0; i < positions.Length; i++) checkpoints[i] = new EezTransactionCheckpoint(positions[i], TestItem.KeccakA, TestItem.KeccakB);
        return new EezStatelessBlockResult(block, block.Header, receipts, checkpoints);
    }
}
