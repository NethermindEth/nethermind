// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Channels;
using Autofac.Features.AttributeFilters;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Init.Steps;
using Nethermind.Int256;
using Nethermind.Logging;
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
[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(InitializeBlockchain)]
)]
public class ImportPbtFromPreimageFlat(
    FlatPersistence flatSource,
    [KeyFilter(DbNames.Code)] IDb codeDb,
    PbtRebuilder rebuilder,
    IPbtPersistence pbtPersistence,
    IProcessExitSource exitSource,
    ILogManager logManager
) : IStep
{
    private const int AddressLength = 20;
    private const int ChannelCapacity = 100_000;

    private readonly ILogger _logger = logManager.GetClassLogger<ImportPbtFromPreimageFlat>();

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

        if (_logger.IsInfo) _logger.Info($"Rebuilding PBT state from preimage-flat database at {sourceState}");

        Channel<RebuildEntry> channel = Channel.CreateBounded<RebuildEntry>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

        // a linked source lets a consumer failure unblock the producer parked on a full channel
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task producer = Task.Run(() => Produce(reader, channel.Writer, cts.Token), cts.Token);

        try
        {
            await rebuilder.Rebuild(channel.Reader, sourceState.BlockNumber, cancellationToken);
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
            try { await producer; }
            catch { /* the producer's failure, if any, already surfaced through the consumer above */ }
        }

        exitSource.Exit(0);
    }

    private async Task Produce(FlatPersistence.IPersistenceReader reader, ChannelWriter<RebuildEntry> writer, CancellationToken cancellationToken)
    {
        try
        {
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

                await writer.WriteAsync(RebuildEntry.ForAccount(address, account, code), cancellationToken);

                using FlatPersistence.IFlatIterator storageIterator = reader.CreateStorageIterator(accountKey, ValueKeccak.Zero, ValueKeccak.MaxValue);
                while (storageIterator.MoveNext())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // preimage mode: the flat slot key holds the raw slot as a 32-byte big-endian value
                    UInt256 slot = new(storageIterator.CurrentKey.Bytes, isBigEndian: true);
                    await writer.WriteAsync(RebuildEntry.ForSlot(address, slot, storageIterator.CurrentValue.ToArray()), cancellationToken);
                }
            }

            writer.Complete();
        }
        catch (Exception e)
        {
            writer.Complete(e);
        }
    }

    private static Account DecodeAccount(ReadOnlySpan<byte> slimRlp)
    {
        RlpReader reader = new(slimRlp);
        return AccountDecoder.Slim.Decode(ref reader)!;
    }
}
