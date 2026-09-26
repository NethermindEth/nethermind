// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Text.Json;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class EezStatelessExecutorTests
{
    private const string Block13 = "stateless-block-13";
    private const string Checkpoint2175 = "stateless-checkpoint-2175";
    private const string Window630 = "nonzero-outbound-630";
    private const string Window84 = "captured-devnet-window-84";

    [Test]
    public void Execute_RecordedBlock_ReproducesItsHashAndPostStateRoot()
    {
        EezStatelessBlockResult[] results = Executor(Block13, "chain-config.json")
            .Execute([StatelessFixtures.ReadBlock(Block13, "block-13.rlp", "witness-13.json")], []);

        Assert.That(results[0].Hash, Is.EqualTo(new Hash256("0x16b64a78e9b3e0d533cafe81f9121735f6a2c8122c69b0bb5994ee75fe7bface")),
            "the re-executed block hashes to the recorded block hash");
        Assert.That(results[0].PostStateRoot, Is.EqualTo(new Hash256("0xf09d8f7da5bc5036f8dd9536c953e2212390a46fb3e553ece2b7d419131537b1")),
            "the post-state root derived from the witness matches the recording");
    }

    [TestCase(new[] { 0, 1, 2 }, TestName = "EveryTransaction")]
    [TestCase(new[] { 0, 2 }, TestName = "SparseSelection")]
    public void Execute_SettlingBlockCheckpoints_MatchTheRecordedPrefixRoots(int[] indices)
    {
        JsonElement oracle = StatelessFixtures.ReadJson(Checkpoint2175, "checkpoint-oracle-2175.json");
        Hash256[] expectedRoots = oracle.GetProperty("transaction_state_roots").EnumerateArray().Select(static root => new Hash256(root.GetString()!)).ToArray();
        Hash256 blockHash = new(oracle.GetProperty("block_hash").GetString()!);

        EezStatelessBlockResult result = Executor(Checkpoint2175, "checkpoint-chain-config.json").Execute(
            [StatelessFixtures.ReadBlock(Checkpoint2175, "checkpoint-block-2175.rlp.hex", "checkpoint-witness-2175.json")], indices)[0];

        Assert.That(result.Hash, Is.EqualTo(blockHash), "precondition: the settling block re-executes to its recorded hash");
        Assert.That(result.Checkpoints.Select(static c => c.TransactionIndex), Is.EqualTo(indices), "one checkpoint per selected transaction");
        Assert.That(result.Checkpoints.Select(static c => c.StateRoot), Is.EqualTo(indices.Select(i => expectedRoots[i])),
            "each prefix root equals the state root the composer recorded for that transaction");
        Assert.That(result.Checkpoints[^1].BlockHash, Is.EqualTo(blockHash), "the candidate ending with the last transaction is the block itself");
        Assert.That(result.Checkpoints[..^1].Select(static c => c.BlockHash), Has.None.EqualTo(blockHash).And.Unique,
            "every shorter prefix commits to a distinct candidate block");
    }

    [Test]
    public void Execute_RecordedWindow_ChainsToTheRecordedFinalRoot()
    {
        EezStatelessBlockResult[] results = Executor(Window630, "chain-config.json").Execute(
            Enumerable.Range(626, 5).Select(static n => StatelessFixtures.ReadBlock(Window630, $"block-{n}.rlp.hex", $"witness-{n}.json")).ToArray(), []);

        JsonElement oracle = StatelessFixtures.ReadJson(Window630, "oracle.json");
        Assert.That(results[^1].Hash, Is.EqualTo(new Hash256(oracle.GetProperty("settling_block_hash").GetString()!)),
            "the settling block re-executes to its recorded hash");
        Assert.That(results[^1].PostStateRoot, Is.EqualTo(new Hash256(oracle.GetProperty("final_state_root").GetString()!)),
            "the window ends in the recorded final state root");
    }

    [Test]
    public void Execute_WindowOnEezGenesis_ReproducesTheWindowBoundaries()
    {
        EezStatelessBlockResult[] results = Executor(Window84, "chain-config.json").Execute(
            Enumerable.Range(79, 6).Select(static n => StatelessFixtures.ReadBlock(Window84, $"block-{n}.rlp.hex", $"witness-{n}.json")).ToArray(), []);

        JsonElement oracle = StatelessFixtures.ReadJson(Window84, "oracle.json");
        Assert.That(results[0].Block.ParentHash, Is.EqualTo(new Hash256(oracle.GetProperty("window_pre_block_hash").GetString()!)),
            "the window starts on the recorded parent");
        Assert.That(results[^1].Hash, Is.EqualTo(new Hash256(oracle.GetProperty("window_post_block_hash").GetString()!)),
            "absent request predeploys are called as empty accounts, so every block re-executes to its recorded hash");
    }

    [Test]
    public void Execute_WindowWithAGap_IsRejected()
    {
        EezStatelessBlock[] window = new[] { 79, 81 }.Select(static n => StatelessFixtures.ReadBlock(Window84, $"block-{n}.rlp.hex", $"witness-{n}.json")).ToArray();

        Assert.That(() => Executor(Window84, "chain-config.json").Execute(window, []),
            Throws.TypeOf<EezStatelessException>().With.Property(nameof(EezStatelessException.Failure)).EqualTo(EezStatelessFailure.Rejected),
            "every block must build on the previous one");
    }

    [Test]
    public void Execute_BlockWithTamperedStateRoot_IsRejected()
    {
        EezStatelessBlock block = StatelessFixtures.ReadBlock(Block13, "block-13.rlp", "witness-13.json");
        EezStatelessBlock tampered = block with { Rlp = TamperStateRoot(block.Rlp) };

        Assert.That(() => Executor(Block13, "chain-config.json").Execute([tampered], []),
            Throws.TypeOf<EezStatelessException>().With.Property(nameof(EezStatelessException.Failure)).EqualTo(EezStatelessFailure.Rejected),
            "a header whose state root the witness does not reproduce is invalid");
    }

    [Test]
    public void Execute_WitnessMissingTheParentState_IsRejected()
    {
        EezStatelessBlock block = StatelessFixtures.ReadBlock(Block13, "block-13.rlp", "witness-13.json");
        EezStatelessBlock stripped = block with { Witness = StripState(block) };

        Assert.That(() => Executor(Block13, "chain-config.json").Execute([stripped], []),
            Throws.TypeOf<EezStatelessException>().With.Property(nameof(EezStatelessException.Failure)).EqualTo(EezStatelessFailure.Rejected),
            "a witness that cannot open the parent state root rejects the block");
    }

    [TestCase(new[] { 1, 0 }, TestName = "Unordered")]
    [TestCase(new[] { 3 }, TestName = "OutOfRange")]
    public void Execute_ImpossibleCheckpoints_AreAnInternalInvariant(int[] indices) =>
        Assert.That(() => Executor(Checkpoint2175, "checkpoint-chain-config.json").Execute(
                [StatelessFixtures.ReadBlock(Checkpoint2175, "checkpoint-block-2175.rlp.hex", "checkpoint-witness-2175.json")], indices),
            Throws.TypeOf<EezStatelessException>().With.Property(nameof(EezStatelessException.Failure)).EqualTo(EezStatelessFailure.InternalInvariant),
            "the checkpoint plan is derived by the caller, so an impossible one is a caller bug, not an invalid block");

    private static EezStatelessExecutor Executor(string fixture, string chainConfig)
    {
        ISpecProvider specProvider = StatelessFixtures.ReadSpecProvider(fixture, chainConfig);
        return new EezStatelessExecutor(specProvider, LimboLogs.Instance);
    }

    private static byte[] TamperStateRoot(byte[] rlp)
    {
        Block block = Rlp.Decode<Block>(rlp)!;
        block.Header.StateRoot = Keccak.Compute("tampered");
        return Rlp.Encode(block).Bytes;
    }

    private static Witness StripState(EezStatelessBlock block) => new()
    {
        State = new ArrayPoolList<byte[]>(0),
        Codes = block.Witness.Codes,
        Keys = block.Witness.Keys,
        Headers = block.Witness.Headers,
    };
}
