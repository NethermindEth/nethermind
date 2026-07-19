// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Channels;
using Autofac.Features.AttributeFilters;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Init.Steps;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Pbt.Persistence;
using FlatPersistence = Nethermind.State.Flat.Persistence.IPersistence;
using FlatStateId = Nethermind.State.Flat.StateId;

namespace Nethermind.State.Pbt.Steps;

/// <summary>
/// A one-shot step that rebuilds the PBT state from an existing preimage-flat state database. In
/// preimage-flat layout the flat entries are keyed by the original address/slot, so iterating them
/// yields exactly the account/slot preimages the stem tree needs; a hashed source could not be used.
/// The step iterates the source on a background producer, feeds decoded entries to
/// <see cref="PbtRebuilder"/>, then exits the process (mirroring <c>ImportFlatDb</c>).
/// </summary>
/// <remarks>
/// One producer walks the accounts in order; each account's storage is handed to a pool of workers
/// (a job per account with storage) rather than read inline, so a few accounts with huge storage do
/// not serialize the source read. The account producer and every worker feed one entry channel that
/// the rebuilder drains.
/// </remarks>
[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(InitializeBlockchain)]
)]
public class ImportPbtFromPreimageFlat(
    FlatPersistence flatSource,
    [KeyFilter(DbNames.Code)] IDb codeDb,
    PbtRebuilder rebuilder,
    IPbtPersistence pbtPersistence,
    IPbtConfig config,
    IProcessExitSource exitSource,
    ILogManager logManager
) : IStep
{
    private const int AddressLength = 20;
    private const int EntryChannelCapacity = 100_000;
    private const int StorageJobChannelCapacity = 1_024;

    private readonly ILogger _logger = logManager.GetClassLogger<ImportPbtFromPreimageFlat>();

    /// <summary>An account whose storage a worker reads: its flat column key and its precomputed <see cref="PbtKeyDerivation.AddressKeyHash"/>, so workers do not re-hash the address per job.</summary>
    private readonly record struct StorageJob(ValueHash256 AccountKey, ValueHash256 AddressPrefix);

    public async Task Execute(CancellationToken cancellationToken)
    {
        using (IPbtPersistence.IReader pbtReader = pbtPersistence.CreateReader())
        {
            if (pbtReader.CurrentState != StateId.PreGenesis)
            {
                if (_logger.IsInfo) _logger.Info($"PBT state already populated ({pbtReader.CurrentState}); skipping preimage-flat import.");
                return;
            }
        }

        using FlatPersistence.IPersistenceReader reader = flatSource.CreateReader();
        if (!reader.IsPreimageMode)
        {
            if (_logger.IsError) _logger.Error("Source flat database is not in preimage mode; addresses and slots cannot be recovered to build PBT. Aborting.");
            exitSource.Exit(1);
            return;
        }

        FlatStateId sourceState = reader.CurrentState;
        if (sourceState == FlatStateId.PreGenesis)
        {
            if (_logger.IsInfo) _logger.Info("Source flat database is empty; nothing to import.");
            return;
        }

        int workerCount = config.ImportStorageReadConcurrency > 0 ? config.ImportStorageReadConcurrency : Environment.ProcessorCount;
        if (_logger.IsInfo) _logger.Info($"Rebuilding PBT state from preimage-flat database at {sourceState} with {workerCount} storage reader(s)");

        // accounts and every worker's slots share one channel (multiple writers); the rebuilder is the
        // sole reader. Storage jobs — one per account that has storage — fan out to the worker pool.
        Channel<RebuildEntry> entries = Channel.CreateBounded<RebuildEntry>(new BoundedChannelOptions(EntryChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        Channel<StorageJob> storageJobs = Channel.CreateBounded<StorageJob>(new BoundedChannelOptions(StorageJobChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
        });

        // a linked source lets any failure unblock producers parked on a full channel
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task producer = Task.Run(() => ProduceAccounts(reader, entries.Writer, storageJobs.Writer, cts.Token), cts.Token);
        Task[] workers = new Task[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            workers[i] = Task.Run(() => ProduceStorage(storageJobs.Reader, entries.Writer, cts.Token), cts.Token);
        }

        // the entry stream ends only once the account producer and every storage worker have; surface
        // the first failure through the channel so the rebuilder sees it
        Task fanIn = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll([producer, .. workers]);
                entries.Writer.Complete();
            }
            catch (Exception e)
            {
                entries.Writer.Complete(e);
            }
        });

        try
        {
            await rebuilder.Rebuild(entries.Reader, sourceState.BlockNumber, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (_logger.IsInfo) _logger.Info("PBT import cancelled.");
            exitSource.Exit(1);
            return;
        }
        finally
        {
            cts.Cancel();
            try { await fanIn; }
            catch { /* the failure already surfaced through the consumer above */ }
        }

        exitSource.Exit(0);
    }

    private async Task ProduceAccounts(FlatPersistence.IPersistenceReader reader, ChannelWriter<RebuildEntry> entries, ChannelWriter<StorageJob> storageJobs, CancellationToken cancellationToken)
    {
        try
        {
            using ArrayPoolList<RebuildEntry> accountEntries = new(16);
            using FlatPersistence.IFlatIterator accountIterator = reader.CreateAccountIterator(ValueKeccak.Zero, ValueKeccak.MaxValue);
            while (accountIterator.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();

                // preimage mode: the flat key holds the raw address in its first 20 bytes
                ValueHash256 accountKey = accountIterator.CurrentKey;
                Address address = new(accountKey.Bytes[..AddressLength]);
                Account account = DecodeAccount(accountIterator.CurrentValue);

                byte[]? code = account.HasCode
                    ? codeDb.Get(account.CodeHash.Bytes) ?? throw new InvalidDataException($"Missing bytecode for {address} (code hash {account.CodeHash}) in the code database.")
                    : null;

                ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(address);
                accountEntries.Clear();
                RebuildEntry.EmitAccount(address, account, code, addressHash, accountEntries);
                for (int i = 0; i < accountEntries.Count; i++)
                {
                    await entries.WriteAsync(accountEntries[i], cancellationToken);
                }

                // hand the storage to the worker pool rather than reading it inline here
                if (account.HasStorage) await storageJobs.WriteAsync(new StorageJob(accountKey, addressHash), cancellationToken);
            }
        }
        finally
        {
            // whatever happened, no more jobs — let the workers drain and finish
            storageJobs.Complete();
        }
    }

    private async Task ProduceStorage(ChannelReader<StorageJob> storageJobs, ChannelWriter<RebuildEntry> entries, CancellationToken cancellationToken)
    {
        // Each worker reads through its own snapshot: the source is static during import, so the
        // snapshots are consistent, and no reader is shared across threads.
        using FlatPersistence.IPersistenceReader reader = flatSource.CreateReader();
        await foreach (StorageJob job in storageJobs.ReadAllAsync(cancellationToken))
        {
            Address address = new(job.AccountKey.Bytes[..AddressLength]);
            RebuildEntry.SlotDeriver deriver = new(address, job.AddressPrefix);
            using FlatPersistence.IFlatIterator storageIterator = reader.CreateStorageIterator(job.AccountKey, ValueKeccak.Zero, ValueKeccak.MaxValue);
            while (storageIterator.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();

                // preimage mode: the flat slot key holds the raw slot as a 32-byte big-endian value
                UInt256 slot = new(storageIterator.CurrentKey.Bytes, isBigEndian: true);
                EvmWord word = EvmWordSlot.FromStripped(storageIterator.CurrentValue);
                await entries.WriteAsync(deriver.Derive(slot, word), cancellationToken);
            }
        }
    }

    private static Account DecodeAccount(ReadOnlySpan<byte> slimRlp)
    {
        RlpReader reader = new(slimRlp);
        return AccountDecoder.Slim.Decode(ref reader)!;
    }
}
