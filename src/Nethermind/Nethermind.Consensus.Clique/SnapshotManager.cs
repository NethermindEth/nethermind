// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Clique
{
    public class SnapshotManager : ISnapshotManager
    {
        private static byte[] _snapshotBytes = Encoding.UTF8.GetBytes("snapshot-");
        private readonly IBlockTree _blockTree;
        private readonly ICliqueConfig _cliqueConfig;
        private readonly ILogger _logger;
        private readonly LruCache<KeccakKey, Address> _signatures;
        private readonly IEthereumEcdsa _ecdsa;
        private IDb _blocksDb;
        private ulong _lastSignersCount = 0;
        private LruCache<KeccakKey, Snapshot> _snapshotCache = new(Clique.InMemorySnapshots, "clique snapshots");

        public SnapshotManager(ICliqueConfig cliqueConfig, IDb blocksDb, IBlockTree blockTree, IEthereumEcdsa ecdsa, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _cliqueConfig = cliqueConfig ?? throw new ArgumentNullException(nameof(cliqueConfig));
            _signatures = new(Clique.InMemorySignatures, Clique.InMemorySignatures, "signatures");
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _blocksDb = blocksDb ?? throw new ArgumentNullException(nameof(blocksDb));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        }

        public Address GetBlockSealer(BlockHeader header)
        {
            if (header.Author is not null) return header.Author;
            if (header.Number == UInt256.Zero) return Address.Zero;
            if (_signatures.Get(header.Hash) is not null) return _signatures.Get(header.Hash);

            int extraSeal = 65;

            // Retrieve the signature from the header extra-data
            if (header.ExtraData.Length < extraSeal)
            {
                throw new BlockchainException($"Clique block without sealer extra data{Environment.NewLine}{header.ToString(BlockHeader.Format.Full)}");
            }

            Span<byte> signatureBytes = header.ExtraData.AsSpan(header.ExtraData.Length - extraSeal, extraSeal);
            Signature signature = new(signatureBytes);
            signature.V += Signature.VOffset;
            Keccak message = CalculateCliqueHeaderHash(header);
            Address address = _ecdsa.RecoverAddress(signatureBytes, message);
            _signatures.Set(header.Hash, address);
            return address;
        }

        private int CalculateSignersCount(BlockHeader blockHeader)
        {
            int signersCount = (blockHeader.ExtraData.Length - Clique.ExtraVanityLength - Clique.ExtraSealLength) /
                               Address.ByteLength;
            _lastSignersCount = signersCount > 0 ? (ulong)signersCount : 1;
            return signersCount;
        }

        public static Keccak CalculateCliqueHeaderHash(BlockHeader blockHeader)
        {
            int extraSeal = 65;
            int shortExtraLength = blockHeader.ExtraData.Length - extraSeal;
            byte[] fullExtraData = blockHeader.ExtraData;
            byte[] shortExtraData = blockHeader.ExtraData.Slice(0, shortExtraLength);
            blockHeader.ExtraData = shortExtraData;
            Keccak sigHash = blockHeader.CalculateHash();
            blockHeader.ExtraData = fullExtraData;
            return sigHash;
        }

        private object _snapshotCreationLock = new();

        public ulong GetLastSignersCount() => _lastSignersCount;

        public Snapshot GetOrCreateSnapshot(long number, Keccak hash)
        {
            Snapshot? snapshot = GetSnapshot(number, hash);
            if (snapshot is not null)
            {
                return snapshot;
            }

            List<BlockHeader> headers = new List<BlockHeader>();
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
                    header = _blockTree.FindHeader(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                    if (header is null)
                    {
                        throw new InvalidOperationException($"Unknown ancestor ({hash}) of {previousHeader?.ToString(BlockHeader.Format.Short)}");
                    }

                    if (header.Hash is null) throw new InvalidOperationException("Block tree block without hash set");

                    Keccak parentHash = header.ParentHash;
                    if (IsEpochTransition(number))
                    {
                        Snapshot? parentSnapshot = GetSnapshot(number - 1, parentHash);

                        if (_logger.IsInfo) _logger.Info($"Creating epoch snapshot at block {number}");
                        int signersCount = CalculateSignersCount(header);
                        SortedList<Address, long> signers = new SortedList<Address, long>(signersCount, AddressComparer.Instance);
                        Address epochSigner = GetBlockSealer(header);
                        for (int i = 0; i < signersCount; i++)
                        {
                            Address signer = new(header.ExtraData.Slice(Clique.ExtraVanityLength + i * Address.ByteLength, Address.ByteLength));
                            signers.Add(signer, signer == epochSigner ? number : parentSnapshot is null ? 0L : parentSnapshot.Signers.TryGetValue(signer, out long value) ? value : 0L);
                        }

                        snapshot = new Snapshot(number, header.Hash, signers);
                        Store(snapshot);
                        break;
                    }

                    // No snapshot for this header, gather the header and move backward
                    headers.Add(header);
                    number--;
                    hash = header.ParentHash;
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
                        _logger.Info($"At block {number} a signer has been {word} the signer list:{Environment.NewLine}{string.Join(Environment.NewLine, snapshot.Signers.OrderBy(s => s.Key, AddressComparer.Instance).Select(s => $"  Signer {signerIndex++}: " + (KnownAddresses.GoerliValidators.TryGetValue(s.Key, out string value) ? value : s.Key.ToString())))}");
                    }
                }

                _snapshotCache.Set(snapshot.Hash, snapshot);
                // If we've generated a new checkpoint snapshot, save to disk
            }

            if ((ulong)snapshot.Number % Clique.CheckpointInterval == 0 && headers.Count > 0)
            {
                Store(snapshot);
            }

            return snapshot;
        }

        public bool HasSignedRecently(Snapshot snapshot, long number, Address signer)
        {
            long signedAt = snapshot.Signers[signer];
            if (signedAt == 0L) return false;

            return number - signedAt < snapshot.SignerLimit;
        }

        public bool IsValidVote(Snapshot snapshot, Address address, bool authorize)
        {
            bool signer = snapshot.Signers.ContainsKey(address);
            return signer && !authorize || !signer && authorize;
        }

        public bool IsInTurn(Snapshot snapshot, long number, Address signer)
        {
            return (long)number % snapshot.Signers.Count == snapshot.Signers.IndexOfKey(signer);
        }

        private bool IsEpochTransition(long number)
        {
            return (ulong)number % _cliqueConfig.Epoch == 0;
        }

        private Snapshot? GetSnapshot(long number, Keccak hash)
        {
            if (_logger.IsTrace) _logger.Trace($"Getting snapshot for {number}");
            // If an in-memory snapshot was found, use that
            Snapshot? cachedSnapshot = _snapshotCache.Get(hash);
            if (cachedSnapshot is not null) return cachedSnapshot;

            // If an on-disk checkpoint snapshot can be found, use that
            if ((ulong)number % Clique.CheckpointInterval == 0)
            {
                Snapshot? persistedSnapshot = LoadSnapshot(hash);
                if (persistedSnapshot is not null) return persistedSnapshot;
            }

            return null;
        }

        private static Keccak GetSnapshotKey(Keccak blockHash)
        {
            Span<byte> hashBytes = blockHash.Bytes;
            byte[] keyBytes = new byte[hashBytes.Length];
            for (int i = 0; i < _snapshotBytes.Length; i++) keyBytes[i] = (byte)(hashBytes[i] ^ _snapshotBytes[i]);

            return new Keccak(keyBytes);
        }

        private SnapshotDecoder _decoder = new();

        [Todo(Improve.Refactor, "I guess it was only added here because of the use of blocksdb")]
        private Snapshot? LoadSnapshot(Keccak hash)
        {
            Keccak key = GetSnapshotKey(hash);
            byte[]? bytes = _blocksDb.Get(key);
            if (bytes is null) return null;

            return _decoder.Decode(bytes.AsRlpStream());
        }

        private void Store(Snapshot snapshot)
        {
            RlpStream stream = new(_decoder.GetLength(snapshot, RlpBehaviors.None));
            _decoder.Encode(stream, snapshot);
            Keccak key = GetSnapshotKey(snapshot.Hash);
            _blocksDb.Set(key, stream.Data);
        }

        private Snapshot Apply(Snapshot original, List<BlockHeader> headers, ulong epoch)
        {
            // Allow passing in no headers for cleaner code
            if (headers.Count == 0) return original;

            // Sanity check that the headers can be applied
            for (int i = 0; i < headers.Count - 1; i++)
            {
                if (headers[i].Number != original.Number + i + 1)
                {
                    throw new InvalidOperationException("Invalid voting chain");
                }
            }

            // Iterate through the headers and create a new snapshot
            Snapshot snapshot = (Snapshot)original.Clone();
            foreach (BlockHeader header in headers)
            {
                // Remove any votes on checkpoint blocks
                long number = header.Number;
                if ((ulong)number % epoch == 0)
                {
                    snapshot.Votes.Clear();
                    snapshot.Tally.Clear();
                }

                // Resolve the authorization key and check against signers
                Address signer = header.Author;
                if (!snapshot.Signers.ContainsKey(signer)) throw new InvalidOperationException("Unauthorized signer");
                if (HasSignedRecently(snapshot, number, signer)) throw new InvalidOperationException($"Recently signed (trying to sign {number} when last signed {snapshot.Signers[signer]} with {snapshot.Signers.Count} signers)");

                snapshot.Signers[signer] = number;

                // Header authorized, discard any previous votes for the signer
                for (int i = 0; i < snapshot.Votes.Count; i++)
                {
                    Vote vote = snapshot.Votes[i];
                    if (vote.Signer == signer && vote.Address == header.Beneficiary)
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
                if (Cast(snapshot, header.Beneficiary, authorize))
                {
                    Vote vote = new(signer, number, header.Beneficiary, authorize);
                    snapshot.Votes.Add(vote);
                }

                // If the vote passed, update the list of signers
                Tally tally = snapshot.Tally[header.Beneficiary];
                if (tally.Votes > snapshot.Signers.Count / 2)
                {
                    if (tally.Authorize)
                    {
                        snapshot.Signers.Add(header.Beneficiary, 0);
                    }
                    else
                    {
                        snapshot.Signers.Remove(header.Beneficiary);
                    }

                    // Discard any previous votes the deauthorized signer cast
                    for (int i = 0; i < snapshot.Votes.Count; i++)
                    {
                        if (snapshot.Votes[i].Signer == header.Beneficiary)
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
                        if (snapshot.Votes[i].Address == header.Beneficiary)
                        {
                            snapshot.Votes.RemoveAt(i);
                            i--;
                        }
                    }

                    snapshot.Tally.Remove(header.Beneficiary);
                }
            }

            snapshot.Number += headers.Count;

            // was this needed?
            //            snapshot.Hash = headers[headers.Count - 1].CalculateHash();
            snapshot.Hash = headers[^1].Hash;
            return snapshot;
        }

        private bool Cast(Snapshot snapshot, Address address, bool authorize)
        {
            if (!snapshot.Tally.ContainsKey(address))
            {
                snapshot.Tally[address] = new Tally(authorize);
            }

            // Ensure the vote is meaningful
            if (!IsValidVote(snapshot, address, authorize)) return false;

            // Cast the vote into tally
            snapshot.Tally[address].Votes++;
            return true;
        }

        private bool Uncast(Snapshot snapshot, Address address, bool authorize)
        {
            // If there's no tally, it's a dangling vote, just drop
            if (!snapshot.Tally.ContainsKey(address)) return true;

            Tally tally = snapshot.Tally[address];
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
