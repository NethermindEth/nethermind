// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.State.Flat.History.Test.Archive;

/// <summary>
/// A real on-disk chain whose history index was produced by real block processing, and the <c>eth_call</c> entry
/// point that reads it back at a historical block.
/// </summary>
public sealed class ArchiveChainFixture(ArchiveChainShape shape, string? dbPath = null, TimeSpan? settleFor = null) : IDisposable
{
    private const string MarkerFileName = "archive-bench.marker";

    /// <summary>Long enough for background compaction to form the level structure the seek cost depends on.</summary>
    private static readonly TimeSpan DefaultSettleFor = TimeSpan.FromSeconds(15);

    private readonly string _dbPath = dbPath
        ?? Environment.GetEnvironmentVariable("NETHERMIND_ARCHIVE_CALL_BENCH_DB")
        ?? Path.Combine(Path.GetTempPath(), "nm-archive-call-bench");

    private readonly TimeSpan _settleFor = settleFor ?? DefaultSettleFor;
    private ArchiveRpcBlockchain? _chain;

    public IEthRpcModule EthRpcModule => Built.EthRpcModule;
    public ArchiveChainShape Shape => shape;
    public string DbPath => _dbPath;

    /// <summary>The chain head, which is also the persisted flat state and the history watermark.</summary>
    public ulong HeadBlock { get; private set; }

    /// <summary>The block the benchmark calls at: well below the barrier, and past the first full sweep cycle.</summary>
    public ulong QueryBlock { get; private set; }

    private ArchiveRpcBlockchain Built =>
        _chain ?? throw new InvalidOperationException($"{nameof(BuildAsync)} has to run before the chain is used.");

    /// <summary>True when this run had to build the chain rather than reuse a populated directory.</summary>
    public bool WasGenerated { get; private set; }

    public async Task BuildAsync(CancellationToken cancellationToken = default)
    {
        shape.Validate();

        bool reuse = TryReadMarker(out ArchiveChainShape existingShape, out ulong head, out ulong queryBlock);
        switch (reuse)
        {
            case true when existingShape != shape:
                throw new InvalidOperationException($"'{_dbPath}' holds a chain of shape {existingShape} but {shape} was requested. Clear the DB folder.");
            case true:
                HeadBlock = head;
                QueryBlock = queryBlock;
                break;
            default:
                await GenerateAsync(cancellationToken);
                WasGenerated = true;
                break;
        }

        // Always a fresh open, generated or not: cold block cache, everything read from SST as a restarted node
        // would read it.
        _chain = await ArchiveRpcBlockchain.Create(_dbPath, shape, resume: true);
        VerifyResumedHead();
    }

    /// <summary>Runs the read half of the sweep contract at <paramref name="blockNumber"/>.</summary>
    public ResultWrapper<HexBytes> Call(ulong firstSlot, int slotCount, ulong blockNumber) =>
        EthRpcModule.eth_call(
            new LegacyTransactionForRpc
            {
                From = TestItem.AddressA,
                To = StorageSweepContract.Address,
                Input = StorageSweepContract.ReadCallData(firstSlot, slotCount),
                Gas = (ulong)StorageSweepContract.ReadGas(slotCount),
                GasPrice = 0,
            },
            new BlockParameter(blockNumber));

    /// <summary>The highest block history serves. Equals the head once generation finished.</summary>
    public ulong CapturedWatermark => Built.Container.Resolve<HistoryWriter>().LastCapturedBlock;

    public void Dispose() => _chain?.Dispose();

    /// <summary>
    /// Produces one sweep transaction per block in a throwaway chain, then settles and closes it. The marker is
    /// written last, so its presence means the directory holds a complete, cleanly closed chain.
    /// </summary>
    private async Task GenerateAsync(CancellationToken cancellationToken)
    {
        RejectPartialDirectory();

        using (ArchiveRpcBlockchain chain = await ArchiveRpcBlockchain.Create(_dbPath, shape, resume: false))
        {
            await ProduceSweepBlocks(chain, cancellationToken);

            HeadBlock = chain.BlockTree.Head!.Number;
            QueryBlock = shape.QueryBlock;

            SettleLsm(chain);
        }

        WriteMarker();
    }

    /// <summary>
    /// Builds the directory and stops. Any existing one is deleted first, and no chain is left open for reading, so
    /// this is only useful for producing the database ahead of a benchmark run rather than as part of one.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="BuildAsync"/>, which never destroys anything: reusing a populated
    /// directory is the normal case, and overwriting one is a thing the caller has to ask for by name.
    /// </remarks>
    public async Task RegenerateAsync(CancellationToken cancellationToken = default)
    {
        shape.Validate();

        if (Directory.Exists(_dbPath)) Directory.Delete(_dbPath, recursive: true);

        await GenerateAsync(cancellationToken);
        WasGenerated = true;
    }

    /// <summary>
    /// Generation suggests genesis, so it needs an empty directory. Only reached once <see cref="TryReadMarker"/>
    /// has failed, so any content here is a run that crashed before it could write the marker. Generating on top of
    /// it would restart the sweep windows at slot 0, so they would no longer line up with block numbers — and the
    /// marker written at the end would stamp that as valid.
    /// </summary>
    private void RejectPartialDirectory()
    {
        // Not a marker check: the caller already knows there is no usable marker. This only asks whether data is
        // present regardless.
        if (!Directory.Exists(_dbPath) || Directory.GetFileSystemEntries(_dbPath).Length == 0) return;

        throw new InvalidOperationException(
            $"'{_dbPath}' holds data but no complete marker, so an earlier generation did not finish. Delete the " +
            "directory, or point NETHERMIND_ARCHIVE_CALL_BENCH_DB somewhere else.");
    }

    private async Task ProduceSweepBlocks(ArchiveRpcBlockchain chain, CancellationToken cancellationToken)
    {
        ulong nonce = chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressA);
        ulong txGasLimit = shape.BlockGasLimit - 100_000;
        IReceiptFinder receipts = chain.ReceiptFinder;

        for (int i = 0; i < shape.Blocks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Transaction sweep = Build.A.Transaction
                .WithTo(StorageSweepContract.Address)
                .WithData(StorageSweepContract.WriteCallData(shape.FirstSlotAt(i), shape.SlotsPerBlock))
                .WithGasLimit(txGasLimit)
                .WithGasPrice(1)
                .WithNonce(nonce++)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            Block block = await chain.AddBlock(sweep);
            VerifySweepSucceeded(receipts, block, i);

            // Capture only runs when the flat state persists. Left alone, persistence waits for the ~256-block
            // backstop (there is no consensus layer here to advance finality), so the barrier would trail the head
            // by that much and where it sat would depend on timing.
            if ((i + 1) % shape.FlushEveryBlocks == 0) chain.WorldStateManager.FlushCache(CancellationToken.None);
        }

        // Brings the barrier all the way to the head, so every block below it is served from history.
        chain.WorldStateManager.FlushCache(CancellationToken.None);
    }

    /// <summary>
    /// An out-of-gas sweep is still included in its block, writes no storage, and would leave the history index
    /// nearly empty while the benchmark still ran and reported numbers. Fail loudly instead.
    /// </summary>
    private static void VerifySweepSucceeded(IReceiptFinder receipts, Block block, int blockIndex)
    {
        TxReceipt[] found = receipts.Get(block, recover: false, recoverSender: false);
        if (found is { Length: 1 } && found[0].StatusCode == StatusCode.Success) return;

        string status = found is { Length: > 0 } ? found[0].StatusCode.ToString(CultureInfo.InvariantCulture) : "no receipt";
        throw new InvalidOperationException(
            $"The sweep transaction of generated block {blockIndex} (block {block.Number}) did not succeed " +
            $"(status: {status}, gas used {block.GasUsed} of {block.GasLimit}). It writes no storage when it fails, " +
            $"so the history index would come out empty. Lower {nameof(ArchiveChainShape.SlotsPerBlock)}, or raise " +
            $"the per-slot gas allowance in {nameof(StorageSweepContract)}.");
    }

    /// <summary>
    /// Flushes and lets background compaction settle. Deliberately does <em>not</em> call <c>Compact()</c>: a full
    /// manual compaction collapses everything into one level, the one shape that would hide the per-seek cost this
    /// fixture exists to measure.
    /// </summary>
    private void SettleLsm(ArchiveRpcBlockchain chain)
    {
        IColumnsDb<FlatHistoryColumns> history = chain.Container.Resolve<IColumnsDb<FlatHistoryColumns>>();
        history.Flush();

        if (_settleFor <= TimeSpan.Zero) return;

        // Best-effort: RocksDB compaction is asynchronous and exposes no drain signal. The wait only has to be long
        // enough for the level structure to form; a short one biases toward more L0 files, not fewer.
        Thread.Sleep(_settleFor);
        history.Flush();
    }

    private void VerifyResumedHead()
    {
        ulong? actualHead = Built.BlockTree.Head?.Number;
        if (actualHead == HeadBlock) return;

        throw new InvalidOperationException(
            $"'{_dbPath}' reopened at block {actualHead?.ToString(CultureInfo.InvariantCulture) ?? "none"} but a head " +
            $"of {HeadBlock} was expected. The directory is not a complete generated chain; delete it and rebuild.");
    }

    private string MarkerPath => Path.Combine(_dbPath, MarkerFileName);

    private void WriteMarker() => File.WriteAllLines(MarkerPath,
    [
        shape.Blocks.ToString(CultureInfo.InvariantCulture),
        shape.SlotsPerBlock.ToString(CultureInfo.InvariantCulture),
        shape.TotalSlots.ToString(CultureInfo.InvariantCulture),
        shape.FlushEveryBlocks.ToString(CultureInfo.InvariantCulture),
        HeadBlock.ToString(CultureInfo.InvariantCulture),
        QueryBlock.ToString(CultureInfo.InvariantCulture),
    ]);

    private bool TryReadMarker(out ArchiveChainShape existingShape, out ulong head, out ulong queryBlock)
    {
        existingShape = default;
        head = 0;
        queryBlock = 0;

        // Written last, so its absence also covers a generation that crashed half way.
        if (!File.Exists(MarkerPath)) return false;

        string[] lines = File.ReadAllLines(MarkerPath);
        if (lines.Length < 6) return false;

        existingShape = new ArchiveChainShape(
            int.Parse(lines[0], CultureInfo.InvariantCulture),
            int.Parse(lines[1], CultureInfo.InvariantCulture),
            int.Parse(lines[2], CultureInfo.InvariantCulture),
            int.Parse(lines[3], CultureInfo.InvariantCulture));
        head = ulong.Parse(lines[4], CultureInfo.InvariantCulture);
        queryBlock = ulong.Parse(lines[5], CultureInfo.InvariantCulture);
        return true;
    }
}
