// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test.Archive;

/// <summary>
/// Proves the generated chain the archive-index benchmark measures is actually readable, and that a historical
/// <c>eth_call</c> reaches the history index rather than the head state.
/// </summary>
[TestFixture]
[Explicit("Benchmark harness: generates a real chain on disk. Run when touching the archive-index benchmark.")]
public class ArchiveEthCallTests
{
    private static readonly ArchiveChainShape Shape = ArchiveChainShape.Tiny;

    private string _dbPath = null!;

    [SetUp]
    public void SetUp() => _dbPath = Path.Combine(Path.GetTempPath(), "nm-archive-call-test", Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_dbPath)) Directory.Delete(_dbPath, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a run over.
        }
    }

    [Test]
    public async Task Historical_call_reads_the_value_of_its_own_block_not_the_head()
    {
        using ArchiveChainFixture fixture = NewFixture();
        await fixture.BuildAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.WasGenerated, Is.True);
            // Capture is driven by persistence; a watermark short of the head means the flush pass never ran.
            Assert.That(fixture.CapturedWatermark, Is.EqualTo(fixture.HeadBlock), "history watermark");
            Assert.That(fixture.QueryBlock, Is.LessThan(fixture.HeadBlock), "query block must sit below the barrier");
        }

        UInt256 atQueryBlock = ReadWindowSum(fixture, fixture.QueryBlock);
        UInt256 atHead = ReadWindowSum(fixture, fixture.HeadBlock);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(atQueryBlock, Is.EqualTo(ExpectedWindowSum(fixture.QueryBlock)), "historical value");
            Assert.That(atHead, Is.EqualTo(ExpectedWindowSum(fixture.HeadBlock)), "head value");
            // The whole point of the archive index: the two answers must differ.
            Assert.That(atQueryBlock, Is.Not.EqualTo(atHead));
        }
    }

    [Test]
    public async Task Reopened_chain_serves_the_same_historical_call()
    {
        ulong queryBlock;
        UInt256 expected;

        using (ArchiveChainFixture generated = NewFixture())
        {
            await generated.BuildAsync();
            queryBlock = generated.QueryBlock;
            expected = ReadWindowSum(generated, queryBlock);
        }

        using ArchiveChainFixture reopened = NewFixture();
        await reopened.BuildAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reopened.WasGenerated, Is.False, "the populated directory must be reused, not rebuilt");
            Assert.That(reopened.QueryBlock, Is.EqualTo(queryBlock));
            Assert.That(ReadWindowSum(reopened, queryBlock), Is.EqualTo(expected));
        }
    }

    [Test]
    public async Task Rejects_a_directory_generated_with_a_different_shape()
    {
        using (ArchiveChainFixture generated = NewFixture())
        {
            await generated.BuildAsync();
        }

        using ArchiveChainFixture mismatched = new(Shape with { SlotsPerBlock = Shape.SlotsPerBlock * 2 }, _dbPath, TimeSpan.Zero);

        await Assert.ThatAsync(() => mismatched.BuildAsync(), Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public async Task Rejects_a_populated_directory_that_has_no_marker()
    {
        Directory.CreateDirectory(_dbPath);
        await File.WriteAllTextAsync(Path.Combine(_dbPath, "LOG"), "left over from a generation that crashed");

        using ArchiveChainFixture fixture = NewFixture();

        await Assert.ThatAsync(() => fixture.BuildAsync(), Throws.InstanceOf<InvalidOperationException>());
    }

    /// <summary>
    /// Two cycles of two blocks each put the query block on the head. That shape would still generate and still
    /// benchmark, but the "historical" call would read live flat state, so it has to fail before any of that.
    /// </summary>
    [Test]
    public void Rejects_a_shape_whose_query_block_lands_on_the_head()
    {
        ArchiveChainShape degenerate = new(Blocks: 4, SlotsPerBlock: 20, TotalSlots: 40, FlushEveryBlocks: 8);

        Assert.That(degenerate.QueryBlock, Is.EqualTo((ulong)degenerate.Blocks), "the case under test");
        Assert.That(degenerate.Validate, Throws.InstanceOf<ArgumentOutOfRangeException>());
    }

    /// <summary>
    /// Reads the first sweep window. Every slot in one window is written by the same block, so the expected sum is
    /// exactly <c>SlotsPerBlock * blockNumber</c> — no per-slot bookkeeping needed.
    /// </summary>
    private static UInt256 ReadWindowSum(ArchiveChainFixture fixture, ulong blockNumber)
    {
        ResultWrapper<HexBytes> result = fixture.Call(firstSlot: 0, Shape.SlotsPerBlock, blockNumber);
        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success), $"{result.Result.Error}");
        return new UInt256(result.Data.Bytes.Span, isBigEndian: true);
    }

    /// <summary>The block that last wrote window 0 at or below <paramref name="blockNumber"/>, times the window size.</summary>
    private static UInt256 ExpectedWindowSum(ulong blockNumber)
    {
        // Generated block i carries block number i + 1 and writes window (i % PrimingBlocks).
        ulong cycle = (ulong)Shape.PrimingBlocks;
        ulong lastWritingIndex = (blockNumber - 1) / cycle * cycle;
        return (UInt256)(lastWritingIndex + 1) * (UInt256)(ulong)Shape.SlotsPerBlock;
    }

    private ArchiveChainFixture NewFixture() => new(Shape, _dbPath, TimeSpan.Zero);
}
