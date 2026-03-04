// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Monitoring;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Importer;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(InitializeBlockchain)]
)]
public class ImportFlatDb(
    IBlockTree blockTree,
    IPersistence persistence,
    Importer importer,
    IProcessExitSource exitSource,
    ILogManager logManager
): IStep
{
    ILogger _logger = logManager.GetClassLogger<ImportFlatDb>();

    public Task Execute(CancellationToken cancellationToken)
    {
        BlockHeader? head = blockTree.Head?.Header;
        if (head is null) return Task.CompletedTask;

        using (var reader = persistence.CreateReader())
        {
            _logger.Warn($"Current state is {reader.CurrentState}");
            if (reader.CurrentState.blockNumber > 0)
            {
                _logger.Info("Flat db already exist");
                return Task.CompletedTask;
            }
        }

        new NethermindKestrelMetricServer("+", 9999).Start(); // Just give me the metrics!
        _logger.Info($"Copying state {head.ToString(BlockHeader.Format.Short)} with state root {head.StateRoot}");
        importer.Copy(new StateId(head));

        exitSource.Exit(0);

        return Task.CompletedTask;
    }
}

[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(InitializeBlockchain)]
)]
public class GeneratePreimage(
    IBlockTree blockTree,
    IProcessExitSource exitSource,
    [KeyFilter(DbNames.Preimage)] IDb preimageDb,
    [KeyFilter(DbNames.FlatStorage)] IDb flatStorage,
    [KeyFilter(DbNames.FlatAccount)] IDb flatAccount,
    ILogManager logManager
): IStep
{
    ILogger _logger = logManager.GetClassLogger<GeneratePreimage>();

    public Task Execute(CancellationToken cancellationToken)
    {
        BlockHeader? head = blockTree.Head?.Header;
        if (head is null) return Task.CompletedTask;


        Task accountWrites = Task.Run(() =>
        {
            _logger.Info("Writing accounts");

            ISortedKeyValueStore sortedAccounts = (ISortedKeyValueStore)flatAccount;
            var view = sortedAccounts.GetViewBetween(sortedAccounts.FirstKey, sortedAccounts.LastKey);

            int batchSize = 0;
            long totalKey = 0;
            IWriteBatch preimageWriteBatch = preimageDb.StartWriteBatch();
            while (view.MoveNext())
            {
                ValueHash256 asHash = ValueKeccak.Compute(view.CurrentKey); // Key is the 20 byte address

                preimageWriteBatch.PutSpan(asHash.Bytes, view.CurrentKey);

                batchSize++;

                if (batchSize == 1_000_000)
                {
                    batchSize = 0;
                    totalKey++;
                    _logger.Info($"Accounts: {totalKey}");
                    preimageWriteBatch.Dispose();
                    preimageWriteBatch = preimageDb.StartWriteBatch();
                }
            }

            preimageWriteBatch.Dispose();
        });

        Task storageWrites = Task.Run(() =>
        {
            _logger.Info("Writing storages");

            ISortedKeyValueStore sortedStorage = (ISortedKeyValueStore) flatStorage;
            var view = sortedStorage.GetViewBetween(sortedStorage.FirstKey, sortedStorage.LastKey);

            int batchSize = 0;
            long totalKey = 0;
            IWriteBatch preimageWriteBatch = preimageDb.StartWriteBatch();
            while (view.MoveNext())
            {
                ReadOnlySpan<byte> slotKeyPortion = view.CurrentKey[20..];
                ValueHash256 asHash = ValueKeccak.Compute(slotKeyPortion);

                preimageWriteBatch.PutSpan(asHash.Bytes, slotKeyPortion);

                batchSize++;

                if (batchSize == 1_000_000)
                {
                    batchSize = 0;
                    totalKey++;
                    _logger.Info($"Storage: {totalKey}");
                    preimageWriteBatch.Dispose();
                    preimageWriteBatch = preimageDb.StartWriteBatch();
                }
            }

            preimageWriteBatch.Dispose();
        });

        Task.WaitAll(storageWrites, accountWrites);

        exitSource.Exit(0);

        return Task.CompletedTask;
    }
}
