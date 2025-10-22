// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using System.IO.Abstractions;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Era1;
using Nethermind.Logging;


namespace Nethermind.EraE;
public class EraImporter(
    IFileSystem fileSystem,
    IBlockTree blockTree,
    IReceiptStorage receiptStorage,
    IBlockValidator blockValidator,
    ILogManager logManager,
    IEraConfig eraConfig,
    ISyncConfig syncConfig,
    Era1.IEraStoreFactory eraStoreFactory,
    [KeyFilter(DbNames.Blocks)] ITunableDb blocksDb,
    [KeyFilter(DbNames.Receipts)] ITunableDb receiptsDb)
    : Era1.EraImporter(
        fileSystem, 
        blockTree, 
        receiptStorage, 
        blockValidator, 
        logManager, 
        new Era1.EraConfig { MaxEra1Size = eraConfig.MaxEraESize, NetworkName = eraConfig.NetworkName, Concurrency = eraConfig.Concurrency, ImportBlocksBufferSize = eraConfig.ImportBlocksBufferSize, TrustedAccumulatorFile = eraConfig.TrustedAccumulatorFile, From = eraConfig.From, To = eraConfig.To, ImportDirectory = eraConfig.ImportDirectory }, 
        syncConfig, 
        eraStoreFactory, 
        blocksDb, 
        receiptsDb
    )
{
    public new async Task Import(string src, long from, long to, string? accumulatorFile, string? historicalRootsFile, CancellationToken cancellation = default)
    {
        if (!fileSystem.Directory.Exists(src))
            throw new ArgumentException($"Import directory {src} does not exist");
        if (accumulatorFile != null && !fileSystem.File.Exists(accumulatorFile))
            throw new ArgumentException($"Accumulator file {accumulatorFile} not exist");
        if (historicalRootsFile != null && !fileSystem.File.Exists(historicalRootsFile))
            throw new ArgumentException($"Historical roots file {historicalRootsFile} not exist");

        HashSet<ValueHash256>? trustedHistoricalRoots = null;
        if (historicalRootsFile != null)
        {
            trustedHistoricalRoots = (await fileSystem.File.ReadAllLinesAsync(historicalRootsFile, cancellation)).Select(ValueHash256.FromHexString).ToHashSet();
        }

        HashSet<ValueHash256>? trustedAccumulators = null;
        if (accumulatorFile != null)
        {
            trustedAccumulators = (await fileSystem.File.ReadAllLinesAsync(accumulatorFile, cancellation)).Select(EraPathUtils.ExtractHashFromAccumulatorAndCheckSumEntry).ToHashSet();
        }

        IEraStore eraStore = eraStoreFactory.Create(src, trustedAccumulators, trustedHistoricalRoots);

        long lastBlockInStore = eraStore.LastBlock;
        if (to == 0) to = long.MaxValue;
        if (to != long.MaxValue && lastBlockInStore < to)
        {
            throw new EraImportException($"The directory given for import '{src}' have highest block number {lastBlockInStore} which is lower then last requested block {to}.");
        }
        if (to == long.MaxValue)
        {
            to = lastBlockInStore;
        }

        long firstBlockInStore = eraStore.FirstBlock;
        if (from == 0 && firstBlockInStore != 0)
        {
            from = firstBlockInStore;
        }
        else if (from < firstBlockInStore)
        {
            throw new EraImportException($"The directory given for import '{src}' have lowest block number {firstBlockInStore} which is lower then first requested block {from}.");
        }
        if (from > to && to != 0)
            throw new ArgumentException($"Start block ({from}) must not be after end block ({to})");

        long headp1 = (blockTree.Head?.Number ?? 0) + 1;
        if (from > headp1)
        {
            throw new ArgumentException($"Start block ({from}) must not be after block after head ({headp1})");
        }

        receiptsDb.Tune(ITunableDb.TuneType.HeavyWrite);
        blocksDb.Tune(ITunableDb.TuneType.HeavyWrite);

        try
        {
            await ImportInternal(from, to, eraStore, cancellation);
        }
        finally
        {
            receiptsDb.Tune(ITunableDb.TuneType.Default);
            blocksDb.Tune(ITunableDb.TuneType.Default);
        }
    }

}
