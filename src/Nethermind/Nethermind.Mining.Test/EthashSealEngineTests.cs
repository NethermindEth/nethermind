// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Ethash;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mining.Test;

[Parallelizable(ParallelScope.Self)]
public class EthashSealEngineTests
{
    [Test]
    public async Task Can_mine()
    {
        ulong validNonce = 971086423715460064;

        BlockHeader header = new(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, 27, 1, 21000, 1, new byte[] { 1, 2, 3 });
        header.TxRoot = Keccak.Zero;
        header.ReceiptsRoot = Keccak.Zero;
        header.UnclesHash = Keccak.Zero;
        header.StateRoot = Keccak.Zero;
        header.Bloom = Bloom.Empty;

        Block block = new(header);
        EthashSealer ethashSealer = new(new Ethash(LimboLogs.Instance), NullSigner.Instance, LimboLogs.Instance);
        using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromSeconds(600));
        await ethashSealer.MineAsync(cancellationTokenSource.Token, block, validNonce - 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Header.Nonce, Is.EqualTo(validNonce));
            Assert.That(block.Header.MixHash, Is.EqualTo(new Hash256("0x52b96cf62447129c6bd81f835721ee145b948ae3b05ef6eae454cbf69a5bc05d")));
            Assert.That(block.Header.Hash, Is.EqualTo(block.Header.CalculateHash()), "hash of the sealed header");
        }
    }

    [Test, MaxTime(60_000)]
    public async Task Can_cancel()
    {
        ulong badNonce = 971086423715459953; // change if valid

        BlockHeader header = new(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, (UInt256)BigInteger.Pow(2, 32), 1, 21000, 1, new byte[] { 1, 2, 3 });
        header.TxRoot = Keccak.Zero;
        header.ReceiptsRoot = Keccak.Zero;
        header.UnclesHash = Keccak.Zero;
        header.StateRoot = Keccak.Zero;
        header.Bloom = Bloom.Empty;

        Block block = new(header);
        EthashSealer ethashSealer = new(new Ethash(LimboLogs.Instance), NullSigner.Instance, LimboLogs.Instance);
        using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromMilliseconds(2000));
        Task<Block> sealing = ethashSealer.MineAsync(cancellationTokenSource.Token, block, badNonce);

        Task completed = await Task.WhenAny(sealing, Task.Delay(TimeSpan.FromSeconds(50)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed, Is.SameAs(sealing), "sealing stopped");
            Assert.That(sealing.IsCanceled, Is.True, "sealing was cancelled");
        }
    }

    [Test, MaxTime(60_000)]
    public async Task Mine_stops_when_cancelled()
    {
        ulong badNonce = 971086423715459953; // as in Can_cancel
        Ethash ethash = new(LimboLogs.Instance);
        // Build the epoch's cache first, so the cancellation lands in the nonce loop
        ethash.HintRange(Guid.NewGuid(), 1, 1);
        ethash.Validate(MineableHeader(1));
        using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromMilliseconds(200));
        Task mining = Task.Run(() => ethash.Mine(MineableHeader((UInt256)BigInteger.Pow(2, 32)), badNonce, cancellationTokenSource.Token));

        Task completed = await Task.WhenAny(mining, Task.Delay(TimeSpan.FromSeconds(50)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed, Is.SameAs(mining), "mining stopped");
            Assert.That(mining.Exception?.InnerException, Is.InstanceOf<OperationCanceledException>(), "mining was cancelled");
        }
    }

    [Test]
    public void Mine_with_a_cancelled_token_builds_no_cache()
    {
        TestLogger logger = new();
        Ethash ethash = new(new OneLoggerLogManager(new(logger)));
        using CancellationTokenSource cancellationTokenSource = new();
        cancellationTokenSource.Cancel();

        Assert.That(() => ethash.Mine(MineableHeader(1), null, cancellationTokenSource.Token), Throws.InstanceOf<OperationCanceledException>());
        // A cache miss logs at Trace and a cache build at Debug
        Assert.That(logger.LogList, Is.Empty, "cache work logged");
    }

    [Test]
    public void Cancellable_Mine_defaults_to_the_original_member()
    {
        IEthash ethash = new OriginalMembersOnly();
        Assert.That(ethash.Mine(MineableHeader(1), 41, CancellationToken.None).Nonce, Is.EqualTo(42UL));
    }

    [Test, MaxTime(60_000)]
    public async Task Seal_stops_the_mining_call_when_cancelled()
    {
        using ManualResetEventSlim miningStarted = new();
        bool miningSawCancellation = false;
        IEthash ethash = Substitute.For<IEthash>();
        ethash.Mine(null!, null, default).ReturnsForAnyArgs(callInfo =>
        {
            CancellationToken token = callInfo.ArgAt<CancellationToken>(2);
            miningStarted.Set();
            miningSawCancellation = SpinWait.SpinUntil(() => token.IsCancellationRequested, TimeSpan.FromSeconds(5));
            token.ThrowIfCancellationRequested();
            return (Keccak.Zero, 0UL);
        });
        EthashSealer ethashSealer = new(ethash, NullSigner.Instance, LimboLogs.Instance);
        using CancellationTokenSource cancellationTokenSource = new();

        Task<Block> sealing = ethashSealer.SealBlock(new Block(MineableHeader(1)), cancellationTokenSource.Token);
        Assert.That(miningStarted.Wait(TimeSpan.FromSeconds(10)), Is.True, "mining started");
        cancellationTokenSource.Cancel();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => sealing, Throws.InstanceOf<OperationCanceledException>());
            Assert.That(miningSawCancellation, Is.True, "the mining call saw the cancellation");
        }
    }

    [Test]
    public void Seal_reports_a_mining_fault_with_its_cause()
    {
        InvalidOperationException fault = new("mining failed");
        IEthash ethash = Substitute.For<IEthash>();
        ethash.Mine(null!, null, default).ReturnsForAnyArgs(_ => throw fault);
        EthashSealer ethashSealer = new(ethash, NullSigner.Instance, LimboLogs.Instance);

        Assert.That(() => ethashSealer.SealBlock(new Block(MineableHeader(1)), CancellationToken.None),
            Throws.TypeOf<SealEngineException>().With.InnerException.SameAs(fault));
    }

    private static BlockHeader MineableHeader(UInt256 difficulty) =>
        new(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, difficulty, 1, 21000, 1, [1, 2, 3])
        {
            TxRoot = Keccak.Zero,
            ReceiptsRoot = Keccak.Zero,
            UnclesHash = Keccak.Zero,
            StateRoot = Keccak.Zero,
            Bloom = Bloom.Empty
        };

    [Test]
    [Explicit("use just for finding nonces for other tests")]
    public async Task Find_nonce()
    {
        BlockHeader parentHeader = new(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, 131072, 0, 21000, 0, []);
        parentHeader.Hash = parentHeader.CalculateHash();

        BlockHeader blockHeader = new(parentHeader.Hash, Keccak.OfAnEmptySequenceRlp, Address.Zero, 131136, 1, 21000, 1, []);
        blockHeader.Nonce = 7217048144105167954;
        blockHeader.MixHash = new Hash256("0x37d9fb46a55e9dbbffc428f3a1be6f191b3f8eaf52f2b6f53c4b9bae62937105");
        blockHeader.Hash = blockHeader.CalculateHash();
        Block block = new(blockHeader);

        IEthash ethash = new Ethash(LimboLogs.Instance);
        EthashSealer ethashSealer = new(ethash, NullSigner.Instance, LimboLogs.Instance);
        await ethashSealer.MineAsync(CancellationToken.None, block, 7217048144105167954);

        // Mine builds its data set directly, so validation needs the epoch hinted explicitly.
        ethash.HintRange(Guid.NewGuid(), block.Number, block.Number);
        Assert.That(ethash.Validate(block.Header), Is.True);

        Console.WriteLine(block.Header.Nonce);
        Console.WriteLine(block.Header.MixHash);
    }

    // Implements only the members IEthash had before the cancellable Mine
    private sealed class OriginalMembersOnly : IEthash
    {
        public void HintRange(Guid guid, ulong start, ulong end) { }
        public bool Validate(BlockHeader header) => true;
        public (Hash256 MixHash, ulong Nonce) Mine(BlockHeader header, ulong? startNonce = null) => (Keccak.Zero, (startNonce ?? 0) + 1);
    }
}
