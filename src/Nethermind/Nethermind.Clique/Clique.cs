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
using System.Runtime.CompilerServices;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;

[assembly: InternalsVisibleTo("Nethermind.Clique.Test")]

namespace Nethermind.Clique
{
    public class Clique
    {
        private const int CheckpointInterval = 1024;
        private const int InmemorySnapshots = 128;
        internal const int InmemorySignatures = 4096;
        private const int WiggleTime = 500;

        private const int EpochLength = 30000;
        internal const int ExtraVanity = 32;
        internal const int ExtraSeal = 65;
        public const ulong NonceAuthVote = UInt64.MaxValue;
        public const ulong NonceDropVote = 0UL;
        internal const int DiffInTurn = 2;
        internal const int DiffNoTurn = 1;

        private const int AddressLength = 20;

        private readonly BlockTree _blockTree;
        private readonly ILogger _logger;
        private CliqueConfig _config;
        private ISigner _signer;
        private PrivateKey _key;
        private IDb _blocksDb;
        private LruCache<Keccak, Snapshot> _recents = new LruCache<Keccak, Snapshot>(InmemorySnapshots);
        private LruCache<Keccak, Address> _signatures = new LruCache<Keccak, Address>(InmemorySignatures);

        public Clique(CliqueConfig config, ISigner signer, PrivateKey key, IDb blocksDb, BlockTree blockTree, ILogManager logManager)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _blocksDb = blocksDb ?? throw new ArgumentNullException(nameof(blocksDb));
            _signer = signer ?? throw new ArgumentNullException(nameof(signer));
            _blockTree = blockTree ?? throw new ArgumentNullException();
            _key = key ?? throw new ArgumentNullException(nameof(key));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            if (config.Epoch == 0)
            {
                config.Epoch = EpochLength;
            }
        }

        public Block Mine(Block block)
        {
            BlockHeader header = block.Header;

            // Sealing the genesis block is not supported
            UInt256 number = header.Number;
            if (number == 0)
            {
                throw new InvalidOperationException("Can't sign genesis block");
            }

            // For 0-period chains, refuse to seal empty blocks (no reward but would spin sealing)
            if (_config.Period == 0 && block.Transactions.Length == 0)
            {
                return null;
            }

            // Bail out if we're unauthorized to sign a block
            Snapshot snapshot = MakeSnapshot(number - 1, header.ParentHash);
            if (!snapshot.Signers.Contains(_key.Address))
            {
                throw new InvalidOperationException("Not authorized to sign a block");
            }

            // If we're amongst the recent signers, wait for the next block
            foreach (var item in snapshot.Recent)
            {
                UInt256 seen = item.Key;
                Address recent = item.Value;
                if (recent == _key.Address)
                {
                    ulong limit = (ulong)snapshot.Signers.Count / 2 + 1;
                    if (number < limit || seen > number - limit)
                    {
                        return null;
                    }
                }
            }

            // Sweet, the protocol permits us to sign the block, wait for our time
            long currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long delay = (long)header.Timestamp - currentTimestamp;
            if (header.Difficulty == DiffNoTurn)
            {
                int wiggle = snapshot.Signers.Count / 2 + 1 * WiggleTime;
                Random rnd = new Random();
                delay += rnd.Next(wiggle);
            }
            // Sign immediately if the timestamp is in the past
            delay = delay > 0 ? delay : 0;

            // Sign all the things!
            Keccak headerHash = header.HashCliqueHeader();
            Signature signature = _signer.Sign(_key, headerHash);
            // Copy signature bytes (R and S)
            byte[] signatureBytes = signature.Bytes;
            Array.Copy(signatureBytes, 0, header.ExtraData, header.ExtraData.Length - ExtraSeal, signatureBytes.Length);
            // Copy signature's recovery id (V)
            byte recoveryId = signature.V >= 27 ? (byte)(signature.V - 27) : signature.V;
            header.ExtraData[header.ExtraData.Length - 1] = recoveryId;

            // Wait until sealing is terminated or delay timeout.
            System.Threading.Thread.Sleep((int)delay);

            return block;
        }

        public bool ValidateHeader(BlockHeader header)
        {
            UInt256 number = header.Number;
            // Checkpoint blocks need to enforce zero beneficiary
            bool checkpoint = ((ulong)number % _config.Epoch) == 0;
            if (checkpoint && header.Beneficiary != Address.Zero)
            {
                _logger.Warn($"Invalid block beneficiary ({header.Beneficiary}) - should be empty on checkpoint");
                return false;
            }

            // Nonces must be 0x00..0 or 0xff..f, zeroes enforced on checkpoints
            if (header.Nonce != NonceAuthVote && header.Nonce != NonceDropVote)
            {
                _logger.Warn($"Invalid block nonce ({header.Nonce})");
                return false;
            }

            if (checkpoint && header.Nonce != NonceDropVote)
            {
                _logger.Warn($"Invalid block nonce ({header.Nonce}) - should be zeroes on checkpoints");
                return false;
            }

            if (header.ExtraData.Length < ExtraVanity)
            {
                _logger.Warn($"Invalid block extra data length - missing vanity");
            }

            if (header.ExtraData.Length < ExtraVanity + ExtraSeal)
            {
                _logger.Warn($"Invalid block extra data length - missing seal");
            }

            // Ensure that the extra-data contains a signer list on checkpoint, but none otherwise
            int singersBytes = header.ExtraData.Length - ExtraVanity - ExtraSeal;
            if (!checkpoint && singersBytes != 0)
            {
                _logger.Warn($"Invalid block extra-data ({header.ExtraData}) - should be empty on non-checkpoints");
                return false;
            }

            if (checkpoint && singersBytes % AddressLength != 0)
            {
                _logger.Warn($"Invalid block nonce ({header.ExtraData}) - should contain a list of signers on checkpoints");
                return false;
            }

            // Ensure that the mix digest is zero as we don't have fork protection currently
            if (header.MixHash != Keccak.Zero)
            {
                _logger.Warn($"Invalid block mix hash ({header.MixHash}) - should be zeroes");
                return false;
            }

            // Ensure that the block doesn't contain any uncles which are meaningless in PoA
            if (header.OmmersHash != Keccak.OfAnEmptySequenceRlp)
            {
                _logger.Warn($"Invalid block ommers hash ({header.OmmersHash}) - ommers are meaningless in Clique");
                return false;
            }

            if (header.Difficulty != DiffInTurn && header.Difficulty != DiffNoTurn)
            {
                _logger.Warn($"Invalid block difficulty ({header.Difficulty}) - should be {DiffInTurn} or {DiffNoTurn}");
            }

            return ValidateCascadingFields(header);
        }

        public bool ValidateSeal(BlockHeader header)
        {
            UInt256 number = header.Number;
            // Retrieve the snapshot needed to validate this header and cache it
            Snapshot snapshot = MakeSnapshot(number - 1, header.ParentHash);
            // Resolve the authorization key and check against signers
            Address signer = header.GetBlockSealer(_signatures);
            if (!snapshot.Signers.Contains(signer))
            {
                _logger.Warn($"Invalid block signer {signer} - not authorized to sign a block");
                return false;
            }

            foreach (var recent in snapshot.Recent)
            {
                UInt256 seen = recent.Key;
                Address address = recent.Value;
                if (address == signer)
                {
                    // Signer is among recent, only fail if the current block doesn't shift it out
                    ulong limit = (ulong)snapshot.Signers.Count / 2 + 1;
                    if (seen > number - limit)
                    {
                        _logger.Warn($"Invalid block signer {signer} - the signer is among recents");
                        return false;
                    }
                }
            }

            // Ensure that the difficulty corresponds to the turn-ness of the signer
            bool inturn = snapshot.Inturn(header.Number, signer);
            if (inturn && header.Difficulty != DiffInTurn)
            {
                _logger.Warn($"Invalid block difficulty {header.Difficulty} - should be in-turn {DiffInTurn}");
                return false;
            }

            if (!inturn && header.Difficulty != DiffNoTurn)
            {
                _logger.Warn($"Invalid block difficulty {header.Difficulty} - should be no-turn {DiffNoTurn}");
                return false;
            }

            return true;
        }

        internal Snapshot MakeSnapshot(UInt256 number, Keccak hash)
        {
            // Search for a snapshot in memory or on disk for checkpoints
            List<BlockHeader> headers = new List<BlockHeader>();
            Snapshot snapshot = null;
            while (true)
            {
                snapshot = GetSnapshot(number, hash);
                if (snapshot != null)
                {
                    break;
                }

                // If we're at an checkpoint block, make a snapshot if it's known
                Keccak parentHash = _blockTree.FindHeader(hash).ParentHash;
                if (number == 0 || ((ulong)number % _config.Epoch == 0 && _blockTree.FindHeader(parentHash) == null))
                {
                    BlockHeader checkpoint = _blockTree.FindHeader(hash);
                    if (checkpoint != null)
                    {
                        Keccak blockHash = BlockHeader.CalculateHash(checkpoint);
                        Address[] signers = new Address[(checkpoint.ExtraData.Length - ExtraVanity - ExtraSeal) / AddressLength];
                        for (int i = 0; i < signers.Length; i++)
                        {
                            byte[] signerBytes = new byte[AddressLength];
                            Array.Copy(checkpoint.ExtraData, ExtraVanity + i * AddressLength, signerBytes, 0, AddressLength);
                            signers[i] = new Address(signerBytes);
                        }

                        snapshot = Snapshot.NewSnapshot(_config, _signatures, number, blockHash, signers);
                        snapshot.Store(_blocksDb);
                        break;
                    }
                }

                // No snapshot for this header, gather the header and move backward
                BlockHeader header = _blockTree.FindHeader(hash);
                if (header == null)
                {
                    throw new InvalidOperationException("Unknown ancestor");
                }

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

            snapshot = snapshot.Apply(headers);

            _recents.Set(snapshot.Hash, snapshot);
            // If we've generated a new checkpoint snapshot, save to disk
            if ((ulong)snapshot.Number % CheckpointInterval == 0 && headers.Count > 0)
            {
                snapshot.Store(_blocksDb);
            }

            return snapshot;
        }

        private Snapshot GetSnapshot(UInt256 number, Keccak hash)
        {
            // If an in-memory snapshot was found, use that
            Snapshot memorySnapshot = _recents.Get(hash);
            if (memorySnapshot != null)
            {
                return memorySnapshot;
            }

            // If an on-disk checkpoint snapshot can be found, use that
            if ((ulong)number % CheckpointInterval == 0)
            {
                memorySnapshot = Snapshot.LoadSnapshot(_config, _signatures, _blocksDb, hash);
                if (memorySnapshot != null)
                {
                    return memorySnapshot;
                }
            }

            return null;
        }

        private bool ValidateCascadingFields(BlockHeader header)
        {
            UInt256 number = header.Number;
            // Ensure that the block's timestamp isn't too close to it's parent
            BlockHeader parent = _blockTree.FindHeader(header.ParentHash);
            if (parent.Timestamp + _config.Period > header.Timestamp)
            {
                _logger.Warn($"Incorrect block timestamp ({header.Timestamp}) - should have big enough different with parent");
                return false;
            }

            // Retrieve the snapshot needed to validate this header and cache it
            Snapshot snapshot = MakeSnapshot(number - 1, header.ParentHash);

            // If the block is a checkpoint block, validate the signer list
            if ((ulong)number % _config.Epoch == 0)
            {
                byte[] signersBytes = new byte[snapshot.Signers.Count * AddressLength];
                Address[] signers = snapshot.GetSigners();
                for (int i = 0; i < signers.Length; i++)
                {
                    Address signer = signers[i];
                    for (int j = 0; j < AddressLength; j++)
                    {
                        signersBytes[i * AddressLength + j] = signer[j];
                    }
                }

                int extraSuffix = header.ExtraData.Length - ExtraSeal;
                for (int i = 0; i < extraSuffix - ExtraVanity; i++)
                {
                    if (header.ExtraData[ExtraVanity + i] != signersBytes[i])
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}