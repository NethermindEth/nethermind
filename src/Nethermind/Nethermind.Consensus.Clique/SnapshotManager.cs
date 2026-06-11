// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Attributes;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Clique
{
    public class SnapshotManager(
        ICliqueConfig cliqueConfig,
        [KeyFilter(DbNames.Blocks)] IDb blocksDb,
        IBlockTree blockTree,
        IEthereumEcdsa ecdsa,
        ILogManager logManager
        ) : ISnapshotManager
    {
        private static readonly byte[] _snapshotBytes = Encoding.UTF8.GetBytes("snapshot-");
        private readonly IBlockTree _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        private readonly ICliqueConfig _cliqueConfig = cliqueConfig ?? throw new ArgumentNullException(nameof(cliqueConfig));
        private readonly ILogger _logger = logManager?.GetClassLogger<SnapshotManager>() ?? throw new ArgumentNullException(nameof(logManager));
        private readonly LruCache<ValueHash256, Address> _signatures = new(Clique.InMemorySignatures, Clique.InMemorySignatures, "signatures");
        private readonly IEthereumEcdsa _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
        private readonly IDb _blocksDb = blocksDb ?? throw new ArgumentNullException(nameof(blocksDb));
        private ulong _lastSignersCount = 0;
        private readonly LruCache<ValueHash256, Snapshot> _snapshotCache = new(Clique.InMemorySnapshots, "clique snapshots");

        public Address GetBlockSealer(BlockHeader header)
        {
            if (header.Author is not null) return header.Author;
            if (header.Number == 0) return Address.Zero;
            Hash256 hash = header.Hash ?? throw new InvalidOperationException("Clique header hash is not set.");
            Address? cached = _signatures.Get(hash);
            if (cached is not null) return cached;

            int extraSeal = 65;

            // Retrieve the signature from the header extra-data
            if (header.ExtraData.Length < extraSeal)
            {
                throw new BlockchainException($"Clique block without sealer extra data{Environment.NewLine}{header.ToString(BlockHeader.Format.Full)}");
            }

            Span<byte> signatureBytes = header.ExtraData.AsSpan(header.ExtraData.Length - extraSeal, extraSeal);
            Signature signature = new(signatureBytes);
            signature.V += Signature.VOffset;
            ValueHash256 message = CalculateCliqueHeaderHash(header);
            Address address = _ecdsa.RecoverAddress(signature, in message)
                ?? throw new BlockchainException($"Cannot recover Clique block sealer{Environment.NewLine}{header.ToString(BlockHeader.Format.Full)}");
            _signatures.Set(hash, address);
            return address;
        }

        private int CalculateSignersCount(BlockHeader blockHeader)
        {
            int signersCount = (blockHeader.ExtraData.Length - Clique.ExtraVanityLength - Clique.ExtraSealLength) /
                               Address.Size;
            _lastSignersCount = signersCount > 0 ? (ulong)signersCount : 1;
            return signersCount;
        }

        public static ValueHash256 CalculateCliqueHeaderHash(BlockHeader blockHeader)
        {
            byte[] fullExtraData = blockHeader.ExtraData;
            byte[] shortExtraData = SliceExtraSealFromExtraData(blockHeader.ExtraData);
            blockHeader.ExtraData = shortExtraData;
            ValueHash256 sigHash = blockHeader.CalculateValueHash();
            blockHeader.ExtraData = fullExtraData;
            return sigHash;
        }

        public static byte[] SliceExtraSealFromExtraData(byte[] extraData)
        {
            if (extraData.Length < Clique.ExtraSealLength)
                throw new ArgumentException($"Cannot be less than extra seal length ({Clique.ExtraSealLength}).", nameof(extraData));
            return extraData.Slice(0, extraData.Length - Clique.ExtraSealLength);
        }

        private readonly Lock _snapshotCreationLock = new();

        public ulong GetLastSignersCount() => _lastSignersCount;

        public Snapshot GetOrCreateSnapshot(ulong number, Hash256 hash)
        {
            Snapshot? snapshot = GetSnapshot(number, hash);
            if (snapshot is not null)
            {
                return snapshot;
            }

            List<BlockHeader> headers = [];
            lock (_snapshotCreationLock)
            {
                BlockHeader? header = null;
                // Search for a snapshot in memory or on disk for checkpoints
                while (true)
                {
                    snapshot = GetSnapshot(number, hash);
                    if (snapshot is not null) break;

                    // If we're at an checkpoint block, make a snapshot if it's known
                    BlockHeader? previousHeader = header;
                    header = _blockTree.FindHeader(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded)
                        ?? throw new InvalidOperationException($"Unknown ancestor ({hash}) of {previousHeader?.ToString(BlockHeader.Format.Short)}");

                    Hash256 headerHash = header.Hash ?? throw new InvalidOperationException("Block tree block without hash set");
                    Hash256 parentHash = header.ParentHash ?? throw new InvalidOperationException("Block tree block without parent hash set");

                    if (IsEpochTransition(number))
                    {
                        Snapshot? parentSnapshot = GetSnapshot(number - 1, parentHash);

                        if (_logger.IsInfo) _logger.Info($"Creating epoch snapshot at block {number}");
                        int signersCount = CalculateSignersCount(header);
                        SortedList<Address, ulong> signers = new(signersCount, GenericComparer.GetOptimized<Address>());
                        Address epochSigner = GetBlockSealer(header);
                        for (int i = 0; i < signersCount; i++)
                        {
                            Address signer = new(header.ExtraData.Slice(Clique.ExtraVanityLength + i * Address.Size, Address.Size));
                            signers.Add(signer, signer == epochSigner ? number : parentSnapshot is null ? 0UL : parentSnapshot.Signers.TryGetValue(signer, out ulong value) ? value : 0UL);
                        }

                        snapshot = new Snapshot(number, headerHash, signers);
                        Store(snapshot);
                        break;
                    }

                    // No snapshot for this header, gather the header and move backward
                    headers.Add(header);
                    number--;
                    hash = parentHash;
                }

                if (headers.Count > 0)
                {
                    // Previous snapshot found, apply any pending headers on top of it
                    headers.Reverse();

                    for (int i = 0; i < headers.Count; i++)
                    {
                        headers[i].Author ??= GetBlockSealer(headers[i]);
                    }

                    int countBefore = snapshot.Signers.Count;
                    snapshot = Apply(snapshot, headers, _cliqueConfig.Epoch);

                    int countAfter = snapshot.Signers.Count;
                    if (countAfter != countBefore && _logger.IsInfo)
                    {
                        int signerIndex = 0;
                        string word = countAfter > countBefore ? "added to" : "removed from";
                        _logger.Info($"At block {number} a signer has been {word} the signer list:{Environment.NewLine}{string.Join(Environment.NewLine, snapshot.Signers.OrderBy(s => s.Key, GenericComparer.GetOptimized<Address>()).Select(s => $"  Signer {signerIndex++}: " + s.Key))}");
                    }
                }

                _snapshotCache.Set(snapshot.Hash, snapshot);
                // If we've generated a new checkpoint snapshot, save to disk
            }

            if (snapshot.Number % Clique.CheckpointInterval == 0 && headers.Count > 0)
            {
                Store(snapshot);
            }

            return snapshot;
        }

        public bool HasSignedRecently(Snapshot snapshot, ulong number, Address signer)
        {
            ulong signedAt = snapshot.Signers[signer];
            if (signedAt == 0UL) return false;

            return number - signedAt < snapshot.SignerLimit;
        }

        public bool IsValidVote(Snapshot snapshot, Address address, bool authorize)
        {
            bool signer = snapshot.Signers.ContainsKey(address);
            return signer && !authorize || !signer && authorize;
        }

        public bool IsInTurn(Snapshot snapshot, ulong number, Address signer) => number % (ulong)snapshot.Signers.Count == (ulong)snapshot.Signers.IndexOfKey(signer);

        private bool IsEpochTransition(ulong number) => number % _cliqueConfig.Epoch == 0;

        private Snapshot? GetSnapshot(ulong number, Hash256 hash)
        {
            if (_logger.IsTrace) _logger.Trace($"Getting snapshot for {number}");
            // If an in-memory snapshot was found, use that
            Snapshot? cachedSnapshot = _snapshotCache.Get(hash);
            if (cachedSnapshot is not null) return cachedSnapshot;

            // If an on-disk checkpoint snapshot can be found, use that
            if (number % Clique.CheckpointInterval == 0)
            {
                Snapshot? persistedSnapshot = LoadSnapshot(hash);
                if (persistedSnapshot is not null) return persistedSnapshot;
            }

            return null;
        }

        private static Hash256 GetSnapshotKey(Hash256 blockHash)
        {
            Span<byte> hashBytes = blockHash.Bytes;
            byte[] keyBytes = new byte[hashBytes.Length];
            for (int i = 0; i < _snapshotBytes.Length; i++) keyBytes[i] = (byte)(hashBytes[i] ^ _snapshotBytes[i]);

            return new Hash256(keyBytes);
        }

        private readonly SnapshotDecoder _decoder = new();

        [Todo(Improve.Refactor, "I guess it was only added here because of the use of blocksdb")]
        private Snapshot? LoadSnapshot(Hash256 hash)
        {
            byte[]? bytes = _blocksDb.Get(GetSnapshotKey(hash));
            return bytes is null ? null : _decoder.Decode(bytes);
        }

        private void Store(Snapshot snapshot)
        {
            using ArrayPoolSpan<byte> rlp = _decoder.EncodeToArrayPoolSpan(snapshot);
            _blocksDb.PutSpan(GetSnapshotKey(snapshot.Hash).Bytes, rlp);
        }

        private Snapshot Apply(Snapshot original, List<BlockHeader> headers, ulong epoch)
        {
            // Allow passing in no headers for cleaner code
            if (headers.Count == 0) return original;

            // Sanity check that the headers can be applied
            for (int i = 0; i < headers.Count - 1; i++)
            {
                if (headers[i].Number != original.Number + (ulong)i + 1)
                {
                    throw new InvalidOperationException("Invalid voting chain");
                }
            }

            // Iterate through the headers and create a new snapshot
            Snapshot snapshot = (Snapshot)original.Clone();
            foreach (BlockHeader header in headers)
            {
                // Remove any votes on checkpoint blocks
                ulong number = header.Number;
                if (number % epoch == 0)
                {
                    snapshot.Votes.Clear();
                    snapshot.Tally.Clear();
                }

                // Resolve the authorization key and check against signers
                Address signer = header.Author ?? throw new InvalidOperationException($"Clique block header {header.Number} author is not set.");
                if (!snapshot.Signers.TryGetValue(signer, out ulong value)) throw new InvalidOperationException("Unauthorized signer");
                if (HasSignedRecently(snapshot, number, signer)) throw new InvalidOperationException($"Recently signed (trying to sign {number} when last signed {value} with {snapshot.Signers.Count} signers)");

                snapshot.Signers[signer] = number;
                Address beneficiary = header.Beneficiary ?? throw new InvalidOperationException("Clique block header beneficiary is not set.");

                // Header authorized, discard any previous votes for the signer
                for (int i = 0; i < snapshot.Votes.Count; i++)
                {
                    Vote vote = snapshot.Votes[i];
                    if (vote.Signer == signer && vote.Address == beneficiary)
                    {
                        // Uncast the vote from the cached tally
                        Uncast(snapshot, vote.Address, vote.Authorize);
                        // Uncast the vote from the chronological list
                        snapshot.Votes.RemoveAt(i);
                        break;
                    }
                }

                // Tally up the new vote from the signer
                bool authorize = header.Nonce == Clique.NonceAuthVote;
                if (Cast(snapshot, beneficiary, authorize))
                {
                    Vote vote = new(signer, number, beneficiary, authorize);
                    snapshot.Votes.Add(vote);
                }

                // If the vote passed, update the list of signers
                Tally tally = snapshot.Tally[beneficiary];
                if (tally.Votes > snapshot.Signers.Count / 2)
                {
                    if (tally.Authorize)
                    {
                        snapshot.Signers.Add(header.Beneficiary, 0UL);
                    }
                    else
                    {
                        snapshot.Signers.Remove(beneficiary);
                    }

                    // Discard any previous votes the deauthorized signer cast
                    for (int i = 0; i < snapshot.Votes.Count; i++)
                    {
                        if (snapshot.Votes[i].Signer == beneficiary)
                        {
                            // Uncast the vote from the cached tally
                            if (Uncast(snapshot, snapshot.Votes[i].Address, snapshot.Votes[i].Authorize))
                            {
                                // Uncast the vote from the chronological list
                                snapshot.Votes.RemoveAt(i);
                                i--;
                            }
                        }
                    }

                    // Discard any previous votes around the just changed account
                    for (int i = 0; i < snapshot.Votes.Count; i++)
                    {
                        if (snapshot.Votes[i].Address == beneficiary)
                        {
                            snapshot.Votes.RemoveAt(i);
                            i--;
                        }
                    }

                    snapshot.Tally.Remove(beneficiary);
                }
            }

            snapshot.Number += (ulong)headers.Count;

            // was this needed?
            //            snapshot.Hash = headers[headers.Count - 1].CalculateHash();
            snapshot.Hash = headers[^1].Hash ?? throw new InvalidOperationException("Clique block header hash is not set.");
            return snapshot;
        }

        private bool Cast(Snapshot snapshot, Address address, bool authorize)
        {
            ref Tally? value = ref CollectionsMarshal.GetValueRefOrAddDefault(snapshot.Tally, address, out bool exists);
            if (!exists)
            {
                value = new Tally(authorize);
            }

            // Ensure the vote is meaningful
            if (!IsValidVote(snapshot, address, authorize)) return false;

            // Cast the vote into tally ref
            value!.Votes++;
            return true;
        }

        private static bool Uncast(Snapshot snapshot, Address address, bool authorize)
        {
            // If there's no tally, it's a dangling vote, just drop
            if (!snapshot.Tally.TryGetValue(address, out Tally? value)) return true;

            Tally tally = value;
            // Ensure we only revert counted votes
            if (tally.Authorize != authorize) return false;

            // Otherwise revert the vote
            if (tally.Votes > 1)
            {
                tally.Votes--;
            }
            else
            {
                snapshot.Tally.Remove(address);
            }

            return true;
        }
    }
}
