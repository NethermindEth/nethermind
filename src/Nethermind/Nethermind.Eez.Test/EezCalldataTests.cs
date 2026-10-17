// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Int256;
using NUnit.Framework;
using static Nethermind.Eez.Test.TestWords;

namespace Nethermind.Eez.Test;

public class EezCalldataTests
{
    private const string Window84 = "captured-devnet-window-84";
    private const string Call = "(uint16,bool,uint64,address,uint64,address,uint256,bytes)";
    private const string ExpectedCall = "(bytes32," + Call + "[],bytes32,bool,bytes)";
    private const string L2Entry = "(bytes32," + Call + "[]," + ExpectedCall + "[],bytes32,bool,bytes)";
    private const string L2StaticEntry = "(uint256,bytes32," + Call + "[],bytes32,bool,bytes)";
    private const string Entry = "((uint64,int192,bytes32,bytes32)[],bytes32," + Call + "[]," + ExpectedCall + "[],bytes32,uint64,bool,bytes)";
    private const string StaticEntry = "((uint64,bytes32)[],bytes32," + Call + "[],bytes32,uint64,bool,bytes)";

    [TestCase(EezCalldata.PostAndVerifyBatchSelector,
        "postAndVerifyBatch(((uint64,bytes32)[]," + Entry + "[]," + StaticEntry + "[],uint256,uint256,address[],(uint64,uint64[])[],uint256[],bytes,bytes[],uint64,bool))",
        TestName = "PostAndVerifyBatch")]
    [TestCase(EezCalldata.ExecuteIncomingCrossChainCallSelector,
        "executeIncomingCrossChainCall(" + L2Entry + "[]," + L2StaticEntry + "[])",
        TestName = "ExecuteIncomingCrossChainCall")]
    [TestCase(EezCalldata.LoadExecutionTableSelector, "loadExecutionTable(" + L2Entry + "[]," + L2StaticEntry + "[])", TestName = "LoadExecutionTable")]
    public void Selector_MatchesTheSignatureTheTypesDescribe(uint selector, string signature) =>
        Assert.That(BinaryPrimitives.ReadUInt32BigEndian(Keccak.Compute(Encoding.ASCII.GetBytes(signature)).Bytes), Is.EqualTo(selector),
            "the selector commits to the field order and widths the codec writes");

    [Test]
    public void DecodePostAndVerifyBatch_RecordedBatch_ReproducesTheSignedPublicInputsHash()
    {
        JsonElement oracle = StatelessFixtures.ReadJson(Window84, "oracle.json");
        PostBatch batch = EezCalldata.DecodePostAndVerifyBatch(RecordedBatch());
        ValueHash256 vkey = new(oracle.GetProperty("proof_system_vkey").GetString()!);
        RollupProofAssignment[] assignments = [new(oracle.GetProperty("rollup_id").GetUInt64(), [0], [vkey], [])];

        ValueHash256[] hashes = PublicInputs.Compute([EezCalldata.EntryHash(batch.Entries[0])], [], [], batch.CallData, assignments, 1, Address.Zero);

        Assert.That(batch.ProofSystems, Is.EqualTo(new[] { new Address(oracle.GetProperty("proof_system").GetString()!) }),
            "precondition: the batch is verified by the recorded proof system");
        Assert.That(batch.Entries, Has.Length.EqualTo(1), "precondition: the recorded window settles one anchor entry");
        Assert.That(hashes[0], Is.EqualTo(new ValueHash256(oracle.GetProperty("public_inputs_hash").GetString()!)),
            "decoding, entry hashing and public-input hashing reproduce the hash the signer signed on the devnet");
        Assert.That(batch.Entries[0].RollingHash,
            Is.EqualTo(RollingHash.SeedL1([new StateCommitment(1, batch.Entries[0].RollupUpdates[0].CurrentRoot)], default)),
            "an anchor entry's rolling hash is the L1 seed of its state update with no proxy entry");
    }

    [TestCaseSource(nameof(NonCanonicalBatches))]
    public void DecodePostAndVerifyBatch_NonCanonicalCalldata_IsRejected(Func<byte[], byte[]> mutate) =>
        Assert.That(() => EezCalldata.DecodePostAndVerifyBatch(mutate(RecordedBatch())), Throws.TypeOf<EezAbiException>(),
            "a signature must cover exactly the bytes that were decoded");

    [Test]
    public void EntryHash_PinnedEntries_MatchTheContract()
    {
        ExecutionEntry entry = new([new RollupUpdate(1, Word(0x1111), Word(0x2222), Int256.Int256.Zero)], Word(0x3333), [], [], Word(0x4444), 1, true,
            [0xde, 0xad, 0xbe, 0xef]);
        StaticExecutionEntry staticEntry = new([new ExpectedRoot(1, Word(0x1111))], Word(0x5555), [], Word(0x6666), 1, true, [0xca, 0xfe]);

        Assert.That(EezCalldata.EntryHash(entry), Is.EqualTo(new ValueHash256("0x752aa6c5ddc53a6bfdfec261248ee29246f6e831c59d3567d2c22d80dbf93dc1")),
            "an entry hash is keccak256 of the abi-encoded entry, leading offset word included");
        Assert.That(EezCalldata.StaticEntryHash(staticEntry), Is.EqualTo(new ValueHash256("0x1a63bcaad1cc1d18331cee8e48f0074de3a9f1f887255d3dfdf44f62a08036c3")),
            "a static entry hash is keccak256 of the abi-encoded static entry");
    }

    [Test]
    public void EncodeDecode_BatchWithEveryDynamicShape_RoundTrips()
    {
        CrossChainCall call = new(2, true, 7, new Address("0x00000000000000000000000000000000000000bb"), 3, new Address("0x00000000000000000000000000000000000000aa"),
            UInt256.MaxValue, [1, 2, 3]);
        PostBatch batch = new(
            [new ExpectedRoot(4, Word(0x10))],
            [new ExecutionEntry([new RollupUpdate(1, Word(1), Word(2), new Int256.Int256(-5))], Word(3), [call],
                [new ExpectedCall(Word(4), [call, call], Word(5), false, [9])], Word(6), 1, false, new byte[33])],
            [new StaticExecutionEntry([new ExpectedRoot(1, Word(7))], Word(8), [call], Word(9), 1, true, [])],
            5, 6,
            [new Address("0x00000000000000000000000000000000000000cc")],
            [new RollupProofSystems(1, [0, 2])],
            [7],
            new byte[65],
            [[1], new byte[64]],
            ulong.MaxValue,
            true);

        byte[] encoded = EezCalldata.EncodePostAndVerifyBatch(batch);
        PostBatch decoded = EezCalldata.DecodePostAndVerifyBatch(encoded);

        Assert.That(EezCalldata.EncodePostAndVerifyBatch(decoded), Is.EqualTo(encoded), "decoding then encoding reproduces the calldata");
        Assert.That(decoded.Entries[0].RollupUpdates[0].EtherDelta, Is.EqualTo(new Int256.Int256(-5)), "a negative ether delta survives as two's complement");
        Assert.That(decoded.Entries[0].ExpectedCalls[0].Calls, Has.Length.EqualTo(2), "nested dynamic arrays decode in order");
    }

    [TestCase("00000000000000007fffffffffffffffffffffffffffffffffffffffffffffff", null, TestName = "LargestPositive")]
    [TestCase("ffffffffffffffff800000000000000000000000000000000000000000000000", null, TestName = "SmallestNegative")]
    [TestCase("0000000000000001800000000000000000000000000000000000000000000000", "int192", TestName = "PositiveBeyondTheWidth")]
    [TestCase("ffffffffffffffff7fffffffffffffffffffffffffffffffffffffffffffffff", "int192", TestName = "NegativeNotSignExtended")]
    public void ReadInt192_Word_AcceptsOnlyASignExtendedValue(string word, string? rule)
    {
        byte[] data = Convert.FromHexString(word);

        Action read = () => new AbiReader(data).ReadInt192(0);

        if (rule is null)
        {
            Assert.That(read, Throws.Nothing, "an ether delta within int192 decodes");
        }
        else
        {
            Assert.That(read, Throws.TypeOf<EezAbiException>().With.Message.Contains(rule), "a word whose top bytes do not sign-extend bit 191 is not canonical");
        }
    }

    [Test]
    public void EncodeDecode_L2Calldata_RoundTrips()
    {
        CrossChainCall call = new(0, false, 0, new Address("0x00000000000000000000000000000000000000bb"), 0, EezConstants.Eezl2Address, 1, [4, 5]);
        L2ExecutionEntry entry = new(Word(1), [call], [new ExpectedCall(Word(2), [call], Word(3), true, [6])], Word(4), true, [7]);
        L2StaticExecutionEntry staticEntry = new(1, Word(5), [call], Word(6), false, []);
        IncomingCrossChainCall incoming = new([entry], [staticEntry]);
        ExecutionTable table = new([entry, entry], [staticEntry]);

        byte[] incomingCalldata = EezCalldata.EncodeExecuteIncomingCrossChainCall(incoming);
        byte[] tableCalldata = EezCalldata.EncodeLoadExecutionTable(table);

        Assert.That(EezCalldata.EncodeExecuteIncomingCrossChainCall(EezCalldata.DecodeExecuteIncomingCrossChainCall(incomingCalldata)), Is.EqualTo(incomingCalldata),
            "an inbound delivery decodes and re-encodes identically");
        Assert.That(EezCalldata.EncodeLoadExecutionTable(EezCalldata.DecodeLoadExecutionTable(tableCalldata)), Is.EqualTo(tableCalldata),
            "an execution table decodes and re-encodes identically");
        Assert.That(() => EezCalldata.DecodeLoadExecutionTable(incomingCalldata), Throws.TypeOf<EezAbiException>(),
            "calldata of one entry point is not decoded as another");
    }

    private static TestCaseData[] NonCanonicalBatches()
    {
        const int Selector = 4;
        const int Head = Selector + 32;
        return
        [
            Case(static b => [.. b, 0], "TrailingByte"),
            Case(static b => b[..^1], "Truncated"),
            Case(static b => Set(b, 0, 0x00), "WrongSelector"),
            Case(static b => Set(b, Selector + 31, 0x40), "OuterOffsetNotCanonical"),
            Case(static b => Set(b, Head + 11 * 32 + 31, 2), "BoolOutOfRange"),
            Case(static b => Set(b, Head + 10 * 32, 1), "Uint64OutOfRange"),
            Case(static b => Set(b, Head + 31, 0xff), "InnerOffsetNotCanonical"),
            Case(static b => Set(b, b.Length - 1, 0x01), "NonZeroPaddingOrTail"),
        ];

        static TestCaseData Case(Func<byte[], byte[]> mutate, string name) => new(mutate) { TestName = name };
    }

    private static byte[] Set(byte[] bytes, int index, byte value)
    {
        byte[] copy = (byte[])bytes.Clone();
        copy[index] = value;
        return copy;
    }

    private static byte[] RecordedBatch() => StatelessFixtures.ReadPostBatch(Window84);

}
