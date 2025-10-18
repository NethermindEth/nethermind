// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using System.IO.Abstractions;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Validators;
using Nethermind.Db;
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
    );
