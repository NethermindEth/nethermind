/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;

namespace Nethermind.Clique
{
    public class SnapshotManager : ISnapshotManager
    {
        private readonly ICliqueConfig _cliqueConfig;
        private readonly ICache<Keccak, Address> _signatures;
        private readonly IEthereumSigner _signer;
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;
        private LruCache<Keccak, Snapshot> _recent = new LruCache<Keccak, Snapshot>(Clique.InMemorySnapshots);
        private IDb _blocksDb;

        private bool IsEpochTransition(UInt256 number)
        {
            return (ulong) number % _cliqueConfig.Epoch == 0;
        }

        public SnapshotManager(ICliqueConfig cliqueConfig, IDb blocksDb, IBlockTree blockTree, IEthereumSigner signer, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _cliqueConfig = cliqueConfig ?? throw new ArgumentNullException(nameof(cliqueConfig));
            _signatures = new LruCache<Keccak, Address>(Clique.InMemorySignatures);
            _signer = signer ?? throw new ArgumentNullException(nameof(signer));
            _blocksDb = blocksDb ?? throw new ArgumentNullException(nameof(blocksDb));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        }

        private Snapshot GetSnapshot(UInt256 number, Keccak hash)
        {
            // If an in-memory snapshot was found, use that
            Snapshot cachedSnapshot = _recent.Get(hash);
            if (cachedSnapshot != null)
            {
                return cachedSnapshot;
            }

            // If an on-disk checkpoint snapshot can be found, use that
            if ((ulong) number % Clique.CheckpointInterval == 0)
            {
                Snapshot persistedSnapshot = Snapshot.LoadSnapshot(_signatures, _blocksDb, hash);
                if (persistedSnapshot != null)
                {
                    return persistedSnapshot;
                }
            }

            return null;
        }

        public Address GetBlockSealer(BlockHeader header)
        {
            if (header.Author != null)
            {
                return header.Author;
            }

            if (header.Number == UInt256.Zero)
            {
                return Address.Zero;
            }

            int extraSeal = 65;

            // Retrieve the signature from the header extra-data
            if (header.ExtraData.Length < extraSeal) return null;

            var signatureBytes = header.ExtraData.Slice(header.ExtraData.Length - extraSeal, extraSeal);
            Signature signature = new Signature(signatureBytes);
            signature.V += 27;
            Keccak message = header.HashCliqueHeader();
            Address address = _signer.RecoverAddress(signature, message);
            return address;
        }

        public Snapshot GetOrCreateSnapshot(UInt256 number, Keccak hash)
        {
            // Search for a snapshot in memory or on disk for checkpoints
            List<BlockHeader> headers = new List<BlockHeader>();
            Snapshot snapshot;
            while (true)
            {
                snapshot = GetSnapshot(number, hash);
                if (snapshot != null)
                {
                    break;
                }

                // If we're at an checkpoint block, make a snapshot if it's known
                BlockHeader header = _blockTree.FindHeader(hash);
                if (header == null)
                {
                    throw new InvalidOperationException("Unknown ancestor");
                }

                if (header.Hash == null)
                {
                    throw new InvalidOperationException("Block tree block without hash set");
                }

                Keccak parentHash = header.ParentHash;
                if (number == 0 || (IsEpochTransition(number) && _blockTree.FindHeader(parentHash) == null))
                {
                    int signersCount = header.CalculateSignersCount();
                    SortedList<Address, UInt256> signers = new SortedList<Address, UInt256>(signersCount, CliqueAddressComparer.Instance);
                    for (int i = 0; i < signersCount; i++)
                    {
                        Address signer = new Address(header.ExtraData.Slice(Clique.ExtraVanityLength + i * Address.ByteLength, Address.ByteLength));
                        signers.Add(signer, UInt256.Zero);
                    }

                    snapshot = new Snapshot(_signatures, number, header.Hash, signers);
                    snapshot.Store(_blocksDb);
                    break;
                }

                // No snapshot for this header, gather the header and move backward
                headers.Add(header);
                number = number - 1;
                hash = header.ParentHash;
            }

            // Previous snapshot found, apply any pending headers on top of it
            for (int i = 0; i < headers.Count / 2; i++)
            {
                BlockHeader temp = headers[headers.Count - 1 - i];
                headers[headers.Count - 1 - i] = headers[i];
                headers[i] = temp;
            }

            for (int i = 0; i < headers.Count; i++)
            {
                headers[i].Author = headers[i].Author ?? GetBlockSealer(headers[i]);
            }

            int countBefore = snapshot.Signers.Count;
            snapshot = snapshot.Apply(headers, _cliqueConfig.Epoch);
            int countAfter = snapshot.Signers.Count;

            if (countAfter != countBefore)
            {
                string word = countAfter > countBefore ? "added to" : "removed from";
                _logger.Warn($"A signer has been {word} the signer list - {string.Join(", ", snapshot.Signers.OrderBy(s => s.Key, CliqueAddressComparer.Instance).Select(s => s.Key.ToString()))}");
            }

            _recent.Set(snapshot.Hash, snapshot);
            // If we've generated a new checkpoint snapshot, save to disk
            if ((ulong) snapshot.Number % Clique.CheckpointInterval == 0 && headers.Count > 0)
            {
                snapshot.Store(_blocksDb);
            }

            return snapshot;
        }
    }
}