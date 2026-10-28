// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Follower.RollupX;
using Nethermind.Eez.Prove.V2;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Test;
using NUnit.Framework;
using NSubstitute;

namespace Nethermind.Eez.Test.RollupX;

public class RollupXSettlementInputTests
{
    private const ulong L1ChainId = 10200;
    private readonly EezSettlementContext _context = new(1, 699069, RollupXPublicInputsTests.ProofSystem,
        RollupXPublicInputsTests.VerificationKey(RollupXPublicInputsTests.OperatorSigner), 1);
    private JsonDocument _replay = null!;
    private SettlementInput _input = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _replay = JsonDocument.Parse(File.ReadAllText(StatelessFixtures.PathOf("rollupx/eezdev-699069", "replay-0-2250.json")));
        Assert.That(_replay.RootElement.GetProperty("batches").GetArrayLength(), Is.EqualTo(14), "precondition: all independently accepted batches are present");
        await KzgPolynomialCommitments.InitializeAsync();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _replay.Dispose();

    [SetUp]
    public void SetUp() => _input = Recorded(0, out _, out _);

    [TestCaseSource(nameof(RecordedBatches))]
    public void Decode_RecordedPhysicalBlobs_BindsOriginalHashesAndAcceptedProof(int index)
    {
        SettlementInput input = Recorded(index, out Hash256[] expected, out byte[] proof);
        RollupXSettlementInput decoded = Decode(input);
        ValueHash256 hash = RollupXPostBatchProfile.PublicInputsHash(decoded.Batch, _context.RollupId, _context.VerificationKey, decoded.BlobHashes, decoded.Target);
        Address? signer = new EthereumEcdsa(0).RecoverAddress(new Signature(proof.AsSpan(0, 64), proof[64] - 27), in hash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.BlobHashes, Is.EqualTo(expected), "each exact physical blob derives the transaction's independently recorded versioned hash");
            Assert.That(signer, Is.EqualTo(RollupXPublicInputsTests.OperatorSigner), "the independently accepted proof binds this byte array, order and posting domain");
            Assert.That(decoded.Operations.BlockCount, Is.GreaterThan(0), "the original native stream carries a valid operations payload");
            Assert.That(decoded.L1ParentNumber, Is.EqualTo(input.PostingContext.ParentNumber), "the selected posting predecessor is preserved as an unauthenticated claim");
            Assert.That(decoded.Target.Timestamp, Is.EqualTo(input.PostingContext.TargetTimestamp), "the posting timestamp comes from the exact request");
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    public void Decode_ProoflessCarrier_PreservesPublicInputs(int slots)
    {
        RollupXSettlementInput original = Decode(_input);
        Rewrite(_input, batch => batch with { Proofs = slots == 0 ? [] : [[]] });
        RollupXSettlementInput decoded = Decode(_input);
        ValueHash256 expected = RollupXPostBatchProfile.PublicInputsHash(original.Batch, _context.RollupId, _context.VerificationKey, original.BlobHashes, original.Target);
        ValueHash256 actual = RollupXPostBatchProfile.PublicInputsHash(decoded.Batch, _context.RollupId, _context.VerificationKey, decoded.BlobHashes, decoded.Target);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Batch.Proofs.Length, Is.EqualTo(slots), "proofless input arity is preserved");
            Assert.That(actual, Is.EqualTo(expected), "empty input proof carriers do not change the signed execution commitment");
        }
    }

    [TestCaseSource(nameof(InvalidInputs))]
    public void Decode_MalformedPhysicalInput_IsInvalidArgument(Action<SettlementInput> mutate)
    {
        mutate(_input);
        Invalid(_input);
    }

    [TestCaseSource(nameof(InvalidClaims))]
    public void Decode_CanonicalSettlementClaimMismatch_IsFailedPrecondition(Action<SettlementInput> mutate)
    {
        mutate(_input);
        RpcException error = Assert.Throws<RpcException>(() => Decode(_input))!;
        Assert.That(error.StatusCode, Is.EqualTo(StatusCode.FailedPrecondition), "a canonical but unsupported settlement claim is not malformed encoding");
    }

    [TestCaseSource(nameof(InvalidIndices))]
    public void Decode_BlobSelectionOutsideCompleteOrder_IsInvalidArgument(ulong[] indices)
    {
        SettlementInput input = TwoBlobs();
        Rewrite(input, batch => batch with { BlobIndices = [.. indices.Select(static index => (UInt256)index)] });
        Invalid(input);
    }

    [TestCase(1UL, 2UL, TestName = "Decode_PerBlockBlobCap_Rejects")]
    [TestCase(2UL, 1UL, TestName = "Decode_PerTransactionBlobCap_Rejects")]
    [TestCase(0UL, 2UL, TestName = "Decode_ZeroPerBlockBlobCap_Rejects")]
    [TestCase(2UL, 0UL, TestName = "Decode_ZeroPerTransactionBlobCap_Rejects")]
    public void Decode_TrustedBlobAllowanceExceeded_IsInvalidArgument(ulong blockCap, ulong transactionCap) =>
        Invalid(TwoBlobs(), Provider(Spec(blockCap, transactionCap)));

    [Test]
    public void Decode_L1Before4844_IsInvalidArgument()
    {
        ReleaseSpec spec = new() { MaxBlobCount = 2 };
        spec.IsEip4844Enabled = false;
        Invalid(_input, Provider(spec));
    }

    [Test]
    public void Decode_AtTimestampFork_UsesTargetTimestampAllowance()
    {
        ISpecProvider provider = new CustomSpecProvider(L1ChainId, L1ChainId,
            (new ForkActivation(0), Spec(1, 1)), (new ForkActivation(0, 100), Spec(2, 2)));
        SettlementInput input = TwoBlobs();
        input.PostingContext.ParentNumber = 1;
        input.PostingContext.TargetTimestamp = 99;
        Assert.That(provider.GetSpec(2, 99).MaxBlobsPerTx, Is.EqualTo(1), "precondition: the operator profile limits the earlier target to one blob");
        Invalid(input, provider);
        input.PostingContext.TargetTimestamp = 100;
        Assert.That(RollupXSettlementInput.Decode(input, _context, provider, CancellationToken.None).BlobHashes.Length, Is.EqualTo(2),
            "the new trusted fork activates at the exact target timestamp");
    }

    [Test]
    public void Decode_AtChildHeightFork_UsesParentNumberPlusOne()
    {
        ISpecProvider provider = new CustomSpecProvider(L1ChainId, L1ChainId,
            (new ForkActivation(0), Spec(1, 1)), (new ForkActivation(2), Spec(2, 2)));
        SettlementInput input = TwoBlobs();
        input.PostingContext.ParentNumber = 1;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.GetSpec(1, input.PostingContext.TargetTimestamp).MaxBlobsPerTx, Is.EqualTo(1), "precondition: the parent has the earlier allowance");
            Assert.That(RollupXSettlementInput.Decode(input, _context, provider, CancellationToken.None).BlobHashes.Length, Is.EqualTo(2),
                "the proposed posting block is the parent's child, not the ABI sentinel or the parent itself");
        }
    }

    [Test]
    public void Decode_OriginalWireMutatedAfterwards_RetainsOwnedInputFacts()
    {
        RollupXSettlementInput decoded = Decode(_input);
        ulong timestamp = _input.PostingContext.TargetTimestamp;
        ulong number = _input.PostingContext.ParentNumber;
        Hash256 parent = new(_input.PostingContext.ParentHash.Span);
        byte[] operations = decoded.Stream.Operations.ToArray();
        _input.PostingContext.TargetTimestamp++;
        _input.PostingContext.ParentNumber++;
        _input.PostingContext.ParentHash = ByteString.CopyFrom(TestItem.KeccakA.Bytes);
        _input.AbiCalldata = ByteString.Empty;
        _input.Blobs.Clear();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((decoded.Target.Timestamp, decoded.L1ParentNumber, decoded.Target.ParentHash), Is.EqualTo((timestamp, number, parent)), "posting fields are owned snapshots");
            Assert.That(decoded.Stream.Operations.ToArray(), Is.EqualTo(operations), "the parsed stream retains its own logical backing buffer");
            Assert.That(decoded.BlobHashes.Length, Is.EqualTo(1), "later protobuf mutation cannot replace committed input facts");
        }
    }

    [Test]
    public void Decode_PaddedOuterLengthVarint_PreservesOperationsAndChangesPhysicalHash()
    {
        byte[] operations = Operations();
        byte[] stream = NativeStream.Encode(_context.RollupId, operations, []);
        Assert.That(stream[10], Is.LessThan(128), "precondition: the outer operations length occupies one byte");
        SettlementInput original = StreamInput(stream);
        RollupXSettlementInput originalDecoded = Decode(original);
        byte[] padded = [.. stream.AsSpan(0, 10), (byte)(stream[10] | 0x80), 0, .. stream.AsSpan(11)];
        RollupXSettlementInput decoded = Decode(StreamInput(padded));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Stream.Operations.ToArray(), Is.EqualTo(operations), "the outer EEZ framing permits non-minimal varints");
            Assert.That(decoded.BlobHashes[0], Is.Not.EqualTo(originalDecoded.BlobHashes[0]), "commitments bind physical bytes even when decoded operations match");
        }
    }

    [Test]
    public void Decode_StreamAcrossPhysicalBlobBoundary_PreservesTheWholeOperation()
    {
        Transaction transaction = Build.A.Transaction.WithData(new byte[BlobStream.DataBytesPerBlob + 100]).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        byte[] encoded = TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes;
        byte[] operations = OperationsPayload.Encode([new OperationsRun(1, TestItem.AddressA, ReadOnlyMemory<byte>.Empty, [encoded])]);
        SettlementInput input = StreamInput(NativeStream.Encode(_context.RollupId, operations, []));
        Assert.That(input.Blobs.Count, Is.EqualTo(2), "precondition: one operations message spans two physical blobs");
        RollupXSettlementInput decoded = RollupXSettlementInput.Decode(input, _context, Provider(Spec(2, 2)), CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Stream.Operations.ToArray(), Is.EqualTo(operations), "the stream concatenates physical blobs before decoding messages");
            Assert.That(decoded.Operations.TransactionCount, Is.EqualTo(1), "the canonical ordinary transaction is parsed across the boundary");
            Assert.That(decoded.BlobHashes.Length, Is.EqualTo(2), "both transaction-order blobs contribute a derived commitment");
        }
    }

    [TestCaseSource(nameof(InvalidStreams))]
    public void Decode_MalformedNativeStream_IsInvalidArgument(byte[] stream) => Invalid(StreamInput(stream));

    [Test]
    public void Decode_AlreadyCancelled_DoesNotValidateOrHash() =>
        Assert.That(() => RollupXSettlementInput.Decode(new SettlementInput(), _context, ChiadoSpecProvider.Instance, new CancellationToken(true)),
            Throws.TypeOf<OperationCanceledException>(), "cancellation wins before malformed-input parsing or native commitment work");

    [Test]
    public void Decode_CancelledDuringSpecLookup_StopsBeforeInvalidBlobUnpacking()
    {
        SetTopByte(_input, 0);
        using CancellationTokenSource cancellation = new();
        ISpecProvider provider = Substitute.For<ISpecProvider>();
        provider.ChainId.Returns(L1ChainId);
        provider.GetSpec(Arg.Any<ForkActivation>()).Returns(_ =>
        {
            cancellation.Cancel();
            return Spec(2, 2);
        });
        Assert.That(() => RollupXSettlementInput.Decode(_input, _context, provider, cancellation.Token),
            Throws.TypeOf<OperationCanceledException>(), "controlled cancellation after spec lookup wins before malformed physical packing and native work");
    }

    private RollupXSettlementInput Decode(SettlementInput input) => RollupXSettlementInput.Decode(input, _context, ChiadoSpecProvider.Instance, CancellationToken.None);

    private void Invalid(SettlementInput input, ISpecProvider? provider = null)
    {
        RpcException error = Assert.Throws<RpcException>(() => RollupXSettlementInput.Decode(input, _context, provider ?? ChiadoSpecProvider.Instance, CancellationToken.None))!;
        Assert.That(error.StatusCode, Is.EqualTo(StatusCode.InvalidArgument), "malformed input cannot become a proof or an accepted-parent claim");
    }

    private SettlementInput Recorded(int index, out Hash256[] expected, out byte[] proof)
    {
        JsonElement record = _replay.RootElement.GetProperty("batches")[index];
        JsonElement transaction = record.GetProperty("transaction");
        JsonElement block = record.GetProperty("l1Block");
        PostBatch batch = EezCalldata.DecodePostAndVerifyBatch(Bytes.FromHexString(transaction.GetProperty("input").GetString()!));
        proof = batch.Proofs.Single();
        expected = [.. transaction.GetProperty("blobVersionedHashes").EnumerateArray().Select(static hash => new Hash256(hash.GetString()!))];
        byte[][] slot = [.. record.GetProperty("slotBlobs").EnumerateArray().Select(static hex => PhysicalBlob(hex.GetString()!))];
        byte[][]? selected = BlobSelection.Select(slot, expected);
        Assert.That(selected, Is.Not.Null, "precondition: public recorded slot bytes match all independent transaction blob hashes");
        SettlementInput input = new()
        {
            AbiCalldata = ByteString.CopyFrom(EezCalldata.EncodePostAndVerifyBatch(batch with { Proofs = [[]] })),
            PostingContext = new L1PostingContext
            {
                ChainId = L1ChainId,
                ParentNumber = Convert.ToUInt64(block.GetProperty("number").GetString(), 16) - 1,
                TargetTimestamp = Convert.ToUInt64(block.GetProperty("timestamp").GetString(), 16),
                ParentHash = ByteString.CopyFrom(Bytes.FromHexString(block.GetProperty("parentHash").GetString()!))
            }
        };
        input.Blobs.AddRange(selected!.Select(static blob => ByteString.CopyFrom(blob)));
        return input;
    }

    private SettlementInput TwoBlobs()
    {
        SettlementInput input = StreamInput(NativeStream.Encode(_context.RollupId, Operations(), []));
        input.Blobs.Add(ByteString.CopyFrom(new byte[BlobStream.BytesPerBlob]));
        Rewrite(input, batch => batch with { BlobIndices = [0, 1] });
        return input;
    }

    private SettlementInput StreamInput(byte[] stream)
    {
        SettlementInput input = _input.Clone();
        input.Blobs.Clear();
        input.Blobs.AddRange(BlobStream.Pack(stream).Select(static blob => ByteString.CopyFrom(blob)));
        Rewrite(input, batch => batch with { BlobIndices = [.. Enumerable.Range(0, input.Blobs.Count).Select(static index => (UInt256)index)] });
        return input;
    }

    private static byte[] PhysicalBlob(string trimmed)
    {
        string hex = trimmed[2..];
        byte[] blob = new byte[BlobStream.BytesPerBlob];
        Bytes.FromHexString(hex.Length % 2 == 0 ? hex : hex + "0").CopyTo(blob, 0);
        return blob;
    }

    private static byte[] Operations() => OperationsPayload.Encode([new OperationsRun(1, TestItem.AddressA, ReadOnlyMemory<byte>.Empty, [])]);

    private static void Rewrite(SettlementInput input, Func<PostBatch, PostBatch> mutate) =>
        input.AbiCalldata = ByteString.CopyFrom(EezCalldata.EncodePostAndVerifyBatch(mutate(EezCalldata.DecodePostAndVerifyBatch(input.AbiCalldata.Span))));

    private static ISpecProvider Provider(IReleaseSpec spec) => new CustomSpecProvider(L1ChainId, L1ChainId, (new ForkActivation(0), spec));

    private static IReleaseSpec Spec(ulong blockCap, ulong transactionCap) =>
        new BlobLimitsSpec(new ReleaseSpec { IsEip4844Enabled = true, MaxBlobCount = blockCap }, transactionCap);

    private static IEnumerable<TestCaseData> RecordedBatches()
    {
        for (int i = 0; i < 14; i++) yield return new TestCaseData(i).SetName($"Decode_RecordedBatch{i}_BindsHashesAndAcceptedProof");
    }

    private static IEnumerable<TestCaseData> InvalidInputs()
    {
        yield return Case("MissingPostingContext", static input => input.PostingContext = null);
        yield return Case("ZeroChainId", static input => input.PostingContext.ChainId = 0);
        yield return Case("WrongChainId", static input => input.PostingContext.ChainId = 1);
        yield return Case("ZeroTargetTimestamp", static input => input.PostingContext.TargetTimestamp = 0);
        yield return Case("ParentHeightOverflow", static input => input.PostingContext.ParentNumber = ulong.MaxValue);
        yield return Case("MissingParentHash", static input => input.PostingContext.ParentHash = ByteString.Empty);
        yield return Case("ZeroParentHash", static input => input.PostingContext.ParentHash = ByteString.CopyFrom(new byte[32]));
        yield return Case("ShortParentHash", static input => input.PostingContext.ParentHash = ByteString.CopyFrom(new byte[31]));
        yield return Case("LongParentHash", static input => input.PostingContext.ParentHash = ByteString.CopyFrom(new byte[33]));
        yield return Case("NoBlobs", static input => input.Blobs.Clear());
        yield return Case("ShortPhysicalBlob", static input => input.Blobs[0] = ByteString.CopyFrom(new byte[BlobStream.BytesPerBlob - 1]));
        yield return Case("LongPhysicalBlob", static input => input.Blobs[0] = ByteString.CopyFrom(new byte[BlobStream.BytesPerBlob + 1]));
        yield return Case("NonzeroFirstTopByte", static input => SetTopByte(input, 0));
        yield return Case("NonzeroLastTopByte", static input => SetTopByte(input, BlobStream.BytesPerBlob - 32));
        yield return Case("MissingAbi", static input => input.AbiCalldata = ByteString.Empty);
        yield return Case("TruncatedAbi", static input => input.AbiCalldata = ByteString.CopyFrom(input.AbiCalldata.Span[..^1]));
        yield return Case("TrailingAbiByte", static input => input.AbiCalldata = ByteString.CopyFrom([.. input.AbiCalldata.Span, 0]));
        yield return Case("PopulatedProof", static input => Rewrite(input, static batch => batch with { Proofs = [[1]] }));
        yield return Case("ExtraProofSlot", static input => Rewrite(input, static batch => batch with { Proofs = [[], []] }));
        yield return Case("CalldataTail", static input => Rewrite(input, static batch => batch with { CallData = [1] }));
    }

    private static IEnumerable<TestCaseData> InvalidClaims()
    {
        yield return Claim("LegacyBlockNumber", static batch => batch with { BlockNumber = 0 });
        yield return Claim("SenderBound", static batch => batch with { BindMsgSenderInPublicInput = true });
        yield return Claim("WrongVerifier", static batch => batch with { ProofSystems = [TestItem.AddressA] });
        yield return Claim("WrongRollup", static batch => batch with { RollupIdsWithProofSystems = [new RollupProofSystems(2, [0])] });
    }

    private static TestCaseData Claim(string name, Func<PostBatch, PostBatch> mutate) =>
        new TestCaseData((Action<SettlementInput>)(input => Rewrite(input, mutate))).SetName("Decode_" + name + "_IsFailedPrecondition");

    private static void SetTopByte(SettlementInput input, int offset)
    {
        byte[] blob = input.Blobs[0].ToByteArray();
        blob[offset] = 1;
        input.Blobs[0] = ByteString.CopyFrom(blob);
    }

    private static TestCaseData Case(string name, Action<SettlementInput> mutate) => new TestCaseData(mutate).SetName("Decode_" + name + "_IsInvalidArgument");

    private static IEnumerable<TestCaseData> InvalidIndices()
    {
        yield return new TestCaseData(new ulong[] { 0 }).SetName("Decode_MissingBlobIndex_IsInvalidArgument");
        yield return new TestCaseData(new ulong[] { 1, 0 }).SetName("Decode_ReorderedBlobIndices_IsInvalidArgument");
        yield return new TestCaseData(new ulong[] { 0, 0 }).SetName("Decode_DuplicateBlobIndices_IsInvalidArgument");
        yield return new TestCaseData(new ulong[] { 0, 2 }).SetName("Decode_BlobIndexGap_IsInvalidArgument");
        yield return new TestCaseData(new ulong[] { 0, 1, 2 }).SetName("Decode_ExtraBlobIndex_IsInvalidArgument");
        yield return new TestCaseData(new ulong[] { 1, 2 }).SetName("Decode_MissingZeroBlobIndex_IsInvalidArgument");
    }

    private static IEnumerable<TestCaseData> InvalidStreams()
    {
        byte[] valid = NativeStream.Encode(1, Operations(), []);
        yield return new TestCaseData(Array.Empty<byte>()).SetName("Decode_EmptyNativeStream_IsInvalidArgument");
        yield return new TestCaseData(new byte[] { 255 }).SetName("Decode_UnknownNativeVersion_IsInvalidArgument");
        yield return new TestCaseData(NativeStream.Encode(2, Operations(), [])).SetName("Decode_WrongNativeRollup_IsInvalidArgument");
        yield return new TestCaseData(valid[..^1]).SetName("Decode_MissingNativeClose_IsInvalidArgument");
        yield return new TestCaseData(new byte[] { 0, 3 }).SetName("Decode_MissingChainOperation_IsInvalidArgument");
        yield return new TestCaseData(new byte[] { 0, 2 }).SetName("Decode_TruncatedNativeHeader_IsInvalidArgument");
        yield return new TestCaseData(new byte[] { 0 }).SetName("Decode_TruncatedNativeFrame_IsInvalidArgument");
        yield return new TestCaseData(new byte[] { 0, 2, 1, 0, 0, 0, 0, 0, 0, 0 }).SetName("Decode_MissingOperationsLength_IsInvalidArgument");
        yield return new TestCaseData(new byte[] { 0, 2, 1, 0, 0, 0, 0, 0, 0, 0, 127 }).SetName("Decode_TruncatedOperationsBytes_IsInvalidArgument");
        yield return new TestCaseData((byte[])[.. valid.AsSpan(0, valid.Length - 1), 10, 1]).SetName("Decode_FinishWithoutTransaction_IsInvalidArgument");
        yield return new TestCaseData(NativeStream.Encode(1, [], [])).SetName("Decode_EmptyOperations_IsInvalidArgument");
        yield return new TestCaseData(NativeStream.Encode(1, [255], [])).SetName("Decode_UnknownOperationsVersion_IsInvalidArgument");
        yield return new TestCaseData(NativeStream.Encode(1, [0], [])).SetName("Decode_TruncatedOperations_IsInvalidArgument");
        byte[] tail = [.. valid, 1];
        yield return new TestCaseData(tail).SetName("Decode_NonzeroNativePadding_IsInvalidArgument");
        byte[] operations = Operations();
        byte[] paddedInner = [operations[0], (byte)(operations[1] | 0x80), 0, .. operations.AsSpan(2)];
        yield return new TestCaseData(NativeStream.Encode(1, paddedInner, [])).SetName("Decode_NonMinimalInnerVarint_IsInvalidArgument");
    }
    private sealed class BlobLimitsSpec(IReleaseSpec spec, ulong transactionCap) : ReleaseSpecDecorator(spec)
    {
        public override ulong MaxBlobsPerTx => transactionCap;
    }

}
