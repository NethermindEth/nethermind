// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Collections;
using Nethermind.State.Flat.History.Changesets;
using Nethermind.State.Pbt.Common;

namespace Nethermind.State.Pbt.Image;

/// <summary>Anchor information obtained from the consumer's chain, never from an artifact.</summary>
/// <param name="ActivationTimestamp">The chain specification's binaryTrieTime, or null when it schedules none.
/// An anchor must precede it; there is nothing to precede when it is null.</param>
public sealed record PbtImageAnchor(string ChainId, Hash256 GenesisHash, BlockHeader Header, ulong? ActivationTimestamp)
{
    public void Validate()
    {
        if (ActivationTimestamp is { } activation && Header.Timestamp >= activation || Header.Hash is null || Header.StateRoot is null)
            throw new InvalidDataException("Image requires a pre-activation MPT anchor.");
    }
}

/// <summary>Local buffering budget exhausted; this does not classify an artifact as invalid.</summary>
public sealed class PbtImageResourceLimitException(string message) : Exception(message);

/// <summary>Verifies an EIP-8347 snapshot against the anchor's MPT root through the preimages, as the snapshot streams.</summary>
/// <remarks>
/// The preimages list the anchor's addresses and slot keys in Keccak path order. <see cref="SpoolPreimages"/> first spools them
/// by their PBT tree key, the order the snapshot's leaves ascend in, so <see cref="Join"/> can merge the two streams:
/// every account and slot leaf must be listed, and every listed entry must be a leaf. What the join reads is spooled
/// back into Keccak path order, and <see cref="Verify"/> folds the MPT in a single pass over that spool.
/// </remarks>
public sealed class PbtImageVerifier : IDisposable
{
    private const string ReadPhase = "PBT verify preimages";
    private const string MptPhase = "PBT verify MPT";

    /// <summary>Keccak address path, the account/slot tag, then the Keccak slot path.</summary>
    private const int PathKeyLength = 65;

    private readonly string _directory;
    private readonly ILogManager _logManager;
    private readonly CancellationToken _cancellationToken;
    /// <summary>The listed accounts and slots by tree key: the Keccak address path, then for a slot its Keccak slot path.</summary>
    private readonly SortedSpool _byTreeKey;
    /// <summary>The joined accounts and slots by Keccak path, as the MPT folds them.</summary>
    private readonly SortedSpool _byPath;
    private ulong _accounts;
    private ulong _slots;

    /// <param name="bufferBytes">Sort buffer of each of the two spools.</param>
    public PbtImageVerifier(string scratchDirectory, int bufferBytes, ILogManager logManager, CancellationToken cancellationToken)
    {
        _directory = Path.Combine(scratchDirectory, $"pbt-verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _logManager = logManager;
        _cancellationToken = cancellationToken;
        _byTreeKey = new SortedSpool(_directory, bufferBytes, writerCount: 1, logManager, cancellationToken);
        _byPath = new SortedSpool(_directory, bufferBytes, writerCount: 1, logManager, cancellationToken);
    }

    /// <summary>Spools the preimages by tree key; must precede <see cref="Join"/>.</summary>
    public void SpoolPreimages(Stream preimages)
    {
        float walked = 0;
        using ProgressReporter progress = PbtImageProgress.Start(ReadPhase, "acc", 0, _logManager);
        Func<string> slotCounter = PbtImageProgress.Counter("slot", () => _slots);
        progress.Logger.SetFormat(p => PbtImageProgress.Format(ReadPhase, walked, $"{PbtImageProgress.Counted("acc", p)} | {slotCounter()}"));
        using SortedSpool.Writer writer = _byTreeKey.CreateWriter();
        Span<byte> paths = stackalloc byte[2 * ValueHash256.MemorySize];
        preimages.Position = 0;
        PbtPreimageReader reader = new(preimages);
        while (reader.ReadAccount(out Address? address, out uint slotCount, _cancellationToken))
        {
            ValueHash256 addressKeyHash = Eip8297KeyDerivation.AddressHash(address.ToHash().Bytes);
            ValueHash256 addressHash = reader.AccountHash!.Value;
            walked = PbtImageProgress.KeyspaceFraction(addressHash);
            progress.Update(++_accounts);
            writer.Add(Eip8297KeyDerivation.AccountKey(addressKeyHash, PbtKeyDerivation.BasicDataLeafKey).Bytes, addressHash.Bytes);
            addressHash.Bytes.CopyTo(paths);
            for (uint index = 0; index < slotCount; index++)
            {
                _slots++;
                ValueHash256 slot = reader.ReadSlot(_cancellationToken);
                reader.SlotHash!.Value.Bytes.CopyTo(paths[ValueHash256.MemorySize..]);
                writer.Add(Eip8297KeyDerivation.SlotKey(address.ToHash().Bytes, addressKeyHash, new UInt256(slot.Bytes, isBigEndian: true)).Bytes, paths);
            }
        }
    }

    /// <summary>Passes the ascending snapshot leaves through, joining each account and slot leaf with its listed preimage.</summary>
    /// <remarks>Code leaves carry no preimage and pass unjoined. The leaves are only joined, not validated: the
    /// staging refuses noncanonical ones.</remarks>
    public IEnumerable<RebuildEntry> Join(IEnumerable<RebuildEntry> leaves)
    {
        using SortedSpool.Cursor listed = _byTreeKey.Read();
        using SortedSpool.Writer writer = _byPath.CreateWriter();
        bool hasListed = listed.MoveNext();
        byte[] path = new byte[PathKeyLength];
        ValueHash256? stem = null;
        bool accountListed = false;
        ValueHash256 basicData = default;
        ValueHash256? codeHash = null;
        ValueHash256? delegation = null;

        foreach (RebuildEntry entry in leaves)
        {
            ReadOnlySpan<byte> key = entry.Key.Bytes;
            if (key[0] == Eip8297KeyDerivation.AccountZone)
            {
                ValueHash256 addressHash = Eip8297KeyDerivation.AddressHashOf(entry.Key);
                if (stem != addressHash)
                {
                    FlushAccount();
                    stem = addressHash;
                }
                switch (key[^1])
                {
                    case PbtKeyDerivation.BasicDataLeafKey:
                        Match(entry.Key);
                        listed.Value.CopyTo(path);
                        accountListed = true;
                        basicData = entry.Leaf;
                        hasListed = listed.MoveNext();
                        break;
                    case PbtKeyDerivation.CodeHashLeafKey:
                        codeHash = entry.Leaf;
                        break;
                    case PbtKeyDerivation.DelegationLeafKey:
                        delegation = entry.Leaf;
                        break;
                    default:
                        AddSlot(entry);
                        break;
                }
            }
            else
            {
                FlushAccount();
                if (key[0] != Eip8297KeyDerivation.CodeZone) AddSlot(entry);
            }
            yield return entry;
        }
        FlushAccount();
        if (hasListed) throw new InvalidDataException("Preimages list state the snapshot lacks.");

        void Match(in PbtVariableTreeKey leafKey)
        {
            int order = hasListed ? listed.Key.SequenceCompareTo(leafKey.Bytes) : 1;
            if (order > 0) throw new InvalidDataException("Snapshot holds state its preimages do not list.");
            if (order < 0) throw new InvalidDataException("Preimages list state the snapshot lacks.");
        }

        void AddSlot(in RebuildEntry entry)
        {
            Match(entry.Key);
            listed.Value[..ValueHash256.MemorySize].CopyTo(path);
            path[ValueHash256.MemorySize] = 1;
            listed.Value[ValueHash256.MemorySize..].CopyTo(path.AsSpan(ValueHash256.MemorySize + 1));
            writer.Add(path, Rlp.Encode(entry.Leaf.ToUInt256()).Bytes);
            hasListed = listed.MoveNext();
        }

        void FlushAccount()
        {
            if (stem is null) return;
            if (!accountListed) throw new InvalidDataException("Snapshot holds state its preimages do not list.");
            ValueHash256 codeLeaf = delegation ?? codeHash ?? throw new InvalidDataException("Account has no code-hash leaf.");
            Account account = new PbtAccount(basicData, codeLeaf, IsDelegation: delegation is not null).ToAccount();
            // The address path was copied in when the basic-data leaf was joined.
            path.AsSpan(ValueHash256.MemorySize).Clear();
            writer.Add(path, AccountDecoder.Slim.Encode(account).Bytes);
            stem = null;
            accountListed = false;
            basicData = default;
            codeHash = null;
            delegation = null;
        }
    }

    /// <summary>Folds the joined state into the MPT and refuses it unless it reproduces the anchor's state root.</summary>
    public void Verify(PbtImageAnchor anchor, CancellationToken cancellationToken)
    {
        ILogger logger = _logManager.GetClassLogger(typeof(PbtImageVerifier));
        Stopwatch verifying = Stopwatch.StartNew();
        ValueHash256 mptRoot;
        using (ProgressReporter progress = PbtImageProgress.Start(MptPhase, "acc", _accounts, _logManager))
        using (SortedSpool.Cursor cursor = _byPath.Read())
        using (SortedStateRoot accountsRoot = new())
        using (SortedStateRoot storageRoot = new())
        {
            ulong folded = 0, foldedSlots = 0;
            Func<string> slotCounter = PbtImageProgress.Counter("slot", () => foldedSlots);
            progress.Logger.SetFormat(p => $"{PbtImageProgress.Format(MptPhase, "acc", p)} | {slotCounter()}");
            ValueHash256 addressHash = default;
            Account? account = null;
            while (cursor.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                // An account's tag sorts ahead of its slots', so its slots are the records up to the next account.
                if (cursor.Key[ValueHash256.MemorySize] == 1)
                {
                    foldedSlots++;
                    storageRoot.Add(new ValueHash256(cursor.Key[(ValueHash256.MemorySize + 1)..]), cursor.Value);
                    continue;
                }
                AddAccount();
                progress.Update(++folded);
                addressHash = new ValueHash256(cursor.Key[..ValueHash256.MemorySize]);
                account = AccountDecoder.Slim.Decode(cursor.Value)!;
                storageRoot.Reset();
            }
            AddAccount();
            mptRoot = accountsRoot.Finish();

            void AddAccount()
            {
                if (account is not null)
                    accountsRoot.Add(addressHash, AccountDecoder.Instance.Encode(account.WithChangedStorageRoot(storageRoot.Finish().ToHash256())).Bytes);
            }
        }

        if (mptRoot != anchor.Header.StateRoot!.ValueHash256)
            throw new InvalidDataException("Snapshot does not reproduce the anchor MPT root.");
        if (logger.IsInfo)
            logger.Info($"PBT verified {_accounts:N0} accounts and {_slots:N0} slots against the MPT root {mptRoot} in {verifying.Elapsed:hh\\:mm\\:ss}.");
    }

    public void Dispose()
    {
        _byTreeKey.Dispose();
        _byPath.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
