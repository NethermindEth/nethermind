// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Posting;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class PostBatchPosterTests
{
    private const ulong ChainId = 7331;
    private const ulong RollupId = 2;
    private static readonly Address Registry = new("0x5fbdb2315678afecb367f032d93f642f64180aa3");
    private static readonly PrivateKey PosterKey = TestItem.PrivateKeyA;
    private static readonly UInt256 Tip = 10_000_000_000;
    private static readonly Hash256 PostBatch = Keccak.Compute("postBatch");
    private static readonly ValueHash256 SyncBlock = Keccak.Compute("sync block").ValueHash256;

    private IL1PostingApi _posting = null!;
    private IEezL1Api _l1 = null!;

    [SetUp]
    public void SetUp()
    {
        _posting = Substitute.For<IL1PostingApi>();
        _l1 = Substitute.For<IEezL1Api>();
        _l1.GetLatestBlock(Arg.Any<CancellationToken>()).Returns(new EezL1Block { Number = 100, BaseFeePerGas = 7 });
    }

    [Test]
    public async Task Sign_Calldata_IsAPosterSignedCallToTheRegistry()
    {
        _posting.GetNonce(PosterKey.Address, Arg.Any<CancellationToken>()).Returns(42UL);

        SignedPostBatch signed = await Poster().Sign([0xca, 0xfe], CancellationToken.None);

        Transaction decoded = TxDecoder.Instance.Decode(signed.Raw, RlpBehaviors.SkipTypedWrapping)!;
        Assert.That((decoded.Nonce, decoded.To, decoded.GasLimit, decoded.ChainId), Is.EqualTo((42UL, (Address?)Registry, 16_777_216UL, (ulong?)ChainId)),
            "the poster's confirmed nonce, the registry, the whole postBatch gas budget, the L1 chain");
        Assert.That((decoded.MaxPriorityFeePerGas, decoded.MaxFeePerGas), Is.EqualTo((Tip, 2 * (UInt256)7 + Tip)), "a fee cap of twice the base fee plus the tip");
        Assert.That(new EthereumEcdsa(ChainId).RecoverAddress(decoded), Is.EqualTo(PosterKey.Address), "signed by the poster");
        Assert.That(signed.Hash, Is.EqualTo(Keccak.Compute(signed.Raw)), "the hash L1 will report the transaction under");
    }

    [Test]
    public async Task Sign_AgainAtTheSameNonce_PaysEnoughToReplaceTheFirst()
    {
        _posting.GetNonce(PosterKey.Address, Arg.Any<CancellationToken>()).Returns(42UL);
        PostBatchPoster poster = Poster();
        Transaction first = Decode(await poster.Sign([0x01], CancellationToken.None));

        Transaction second = Decode(await poster.Sign([0x02], CancellationToken.None));

        Assert.That(second.Nonce, Is.EqualTo(first.Nonce), "precondition: the first batch never landed");
        Assert.That((second.MaxPriorityFeePerGas > first.MaxPriorityFeePerGas * 110 / 100, second.MaxFeePerGas > first.MaxFeePerGas * 110 / 100), Is.EqualTo((true, true)),
            "a batch still pending in the mempool at that nonce is replaced, not queued behind");
    }

    [Test]
    public async Task Sign_NextNonce_PaysTheUsualFees()
    {
        _posting.GetNonce(PosterKey.Address, Arg.Any<CancellationToken>()).Returns(42UL, 43UL);
        PostBatchPoster poster = Poster();
        await poster.Sign([0x01], CancellationToken.None);

        Transaction next = Decode(await poster.Sign([0x02], CancellationToken.None));

        Assert.That((next.MaxPriorityFeePerGas, next.MaxFeePerGas), Is.EqualTo((Tip, 2 * (UInt256)7 + Tip)), "nothing to replace once the previous batch landed");
    }

    [Test]
    public async Task Submit_BuilderTakesBundles_SendsNothingToTheMempool()
    {
        _posting.SendBundle(Arg.Any<IReadOnlyList<byte[]>>(), 11, Arg.Any<BundleTarget>(), Arg.Any<CancellationToken>()).Returns(new RpcAnswer<object>("0x1", null, null));

        ulong block = await Poster().Submit([[0x01]], new BundleTarget(11, 1_000), CancellationToken.None);

        Assert.That(block, Is.EqualTo(11), "a pinned bundle targets its slot's block");
        await _posting.DidNotReceiveWithAnyArgs().SendRawTransaction(default!, default);
    }

    [Test]
    public async Task Submit_BuilderWithoutBundles_FallsBackToTheMempoolFromThenOn()
    {
        _posting.SendBundle(Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<ulong>(), Arg.Any<BundleTarget>(), Arg.Any<CancellationToken>())
            .Returns(new RpcAnswer<object>(null, -32601, "method not found"));
        _posting.SendRawTransaction(Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(new RpcAnswer<Hash256>(PostBatch, null, null));
        PostBatchPoster poster = Poster();

        await poster.Submit([[0x01], [0x02]], new BundleTarget(11, 1_000), CancellationToken.None);
        await poster.Submit([[0x03]], new BundleTarget(12, 1_012), CancellationToken.None);

        await _posting.Received(3).SendRawTransaction(Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
        await _posting.Received(1).SendBundle(Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<ulong>(), Arg.Any<BundleTarget>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void Submit_BuilderRefusesTheBundle_Throws()
    {
        _posting.SendBundle(Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<ulong>(), Arg.Any<BundleTarget>(), Arg.Any<CancellationToken>())
            .Returns(new RpcAnswer<object>(null, -32000, "bundle rejected"));

        Assert.ThrowsAsync<PostException>(() => Poster().Submit([[0x01]], new BundleTarget(11, 1_000), CancellationToken.None),
            "a refusal other than an unknown method is the batch's failure, not a reason to leave the builder");
    }

    [TestCase(true, PostOutcome.Settled, TestName = "StoresTheSyncBlock")]
    [TestCase(false, PostOutcome.Failed, TestName = "StoresAnotherBlock")]
    public async Task Observe_BatchLanded_SettledOnlyWhenL1StoresItsSyncBlock(bool storesOurs, PostOutcome expected)
    {
        Hash256 landedIn = Keccak.Compute("L1 block 11");
        _posting.GetReceipt(PostBatch, Arg.Any<CancellationToken>()).Returns(new EezL1Receipt { BlockHash = landedIn, BlockNumber = 11, Status = 1 });
        byte[] stored = storesOurs ? SyncBlock.ToByteArray() : Keccak.Compute("another block").BytesToArray();
        _l1.GetLogs(Registry, L1BatchScanner.L2ExecutionPerformedTopic, L1BatchScanner.RollupTopic(RollupId), 11, 11, Arg.Any<CancellationToken>())
            .Returns([new EezL1Log { BlockHash = landedIn, BlockNumber = 11, Data = [.. stored, .. new byte[32]] }]);

        PostResult result = await Poster().Observe(PostBatch, 11, new BundleTarget(11, 1_000), SyncBlock, CancellationToken.None);

        Assert.That(result, Is.EqualTo(new PostResult(expected, 11)), "a landed batch settles only when L1's commitment is the Sync block it carries, in the block it landed in");
    }

    [TestCase(1_000UL, PostOutcome.Failed, TestName = "BuiltAtThePinWithoutIt")]
    [TestCase(1_004UL, PostOutcome.SlotSkipped, TestName = "BuiltAtAnotherTimestamp")]
    public async Task Observe_PinnedBlockBuiltWithoutTheBatch_TellsAMissFromASkippedSlot(ulong builtAt, PostOutcome expected)
    {
        _l1.GetBlockByNumber(11, Arg.Any<CancellationToken>()).Returns(new EezL1Block { Number = 11, Timestamp = builtAt, Transactions = [Keccak.Compute("other")] });

        PostResult result = await Poster().Observe(PostBatch, 11, new BundleTarget(11, 1_000), SyncBlock, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(expected), "a slot that was never there does not count against the batch's transactions");
    }

    private static Transaction Decode(SignedPostBatch signed) => TxDecoder.Instance.Decode(signed.Raw, RlpBehaviors.SkipTypedWrapping)!;

    private PostBatchPoster Poster() =>
        new(_posting, _l1, new Signer(ChainId, PosterKey, LimboLogs.Instance), new PostingSettings(ChainId, Registry, RollupId, PosterKey.Address, Tip, 16_777_216, true),
            LimboLogs.Instance);
}
