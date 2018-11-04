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
        private const int InmemorySignatures = 4096;
        private const int WiggleTime = 500;

        private const int EpochLength = 30000;
        private const int ExtraVanity = 32;
        public const int ExtraSeal = 65;
        public const ulong NonceAuthVote = UInt64.MaxValue;
        public const ulong NonceDropVote = 0UL;
        private const int DiffInTurn = 2;
        private const int DiffNoTurn = 1;

        private const int AddressLength = 20;

        private readonly BlockTree _blockTree;
        private readonly ILogger _logger;
        private CliqueConfig _config;
        private ISigner _signer;
        private PrivateKey _key;
        private IDb _blocksDb;
        private LruCache<Keccak, Snapshot> _recents = new LruCache<Keccak, Snapshot>(InmemorySnapshots);
        private LruCache<Keccak, Address> _signatures = new LruCache<Keccak, Address>(InmemorySignatures);
        private Dictionary<Address, Boolean> _proposals = new Dictionary<Address, bool>();

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
            ulong number = (ulong) header.Number;
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
            Snapshot snapshot = GetSnapshot(number - 1, header.ParentHash);
            if (!snapshot.Signers.Contains(_key.Address))
            {
                throw new InvalidOperationException("Not authorized to sign a block");
            }

            // If we're amongst the recent signers, wait for the next block
            foreach (var item in snapshot.Recent)
            {
                UInt64 seen = item.Key;
                Address recent = item.Value;
                if (recent == _key.Address)
                {
                    uint limit = (uint) snapshot.Signers.Count / 2 + 1;
                    if (number < limit || seen > (uint) number - limit)
                    {
                        return null;
                    }
                }
            }

            // Sweet, the protocol permits us to sign the block, wait for our time
            long currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long delay = (long) header.Timestamp - currentTimestamp;
            if (header.Difficulty == DiffNoTurn)
            {
                var wiggle = snapshot.Signers.Count / 2 + 1 * WiggleTime;
                Random rnd = new Random();
                delay += rnd.Next(wiggle);
            }

            // Sign all the things!
            // TODO custom rlp encoding
            Keccak headerHash = Keccak.Compute(Rlp.Encode(header));
            Signature signature = _signer.Sign(_key, headerHash);
            byte[] signatureBytes = signature.Bytes;
            Array.Copy(signatureBytes, 0, header.ExtraData, header.ExtraData.Length - ExtraSeal, signatureBytes.Length);

            // Wait until sealing is terminated or delay timeout.
            System.Threading.Thread.Sleep((int) delay);

            return block;
        }

        public bool VerifyHeader(BlockHeader header)
        {
            ulong number = (ulong) header.Number;
            // Checkpoint blocks need to enforce zero beneficiary
            bool checkpoint = ((ulong) number % _config.Epoch) == 0;
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

            // If all checks passed, validate any special fields for hard forks
            // TODO if misc.VerifyForkHashes(chain.Config(), header, false) { return false; }
            return VerifyCascadingFields(header);
        }

        public bool VerifySeal(BlockHeader header)
        {
            ulong number = (ulong) header.Number;
            // Retrieve the snapshot needed to verify this header and cache it
            Snapshot snap = GetSnapshot(number - 1, header.ParentHash);
            // Resolve the authorization key and check against signers
            Address signer = header.GetBlockSealer(_signatures);
            if (!snap.Signers.Contains(signer))
            {
                _logger.Warn($"Invalid block signer {signer} - not authorized to sign a block");
                return false;
            }

            foreach (var recent in snap.Recent)
            {
                UInt64 seen = recent.Key;
                Address address = recent.Value;
                if (address == signer)
                {
                    // Signer is among recent, only fail if the current block doesn't shift it out
                    uint limit = (uint) snap.Signers.Count / 2 + 1;
                    if (seen > (uint) number - limit)
                    {
                        _logger.Warn($"Invalid block signer {signer} - the signer is among recents");
                        return false;
                    }
                }
            }

            // Ensure that the difficulty corresponds to the turn-ness of the signer
            bool inturn = snap.Inturn(header.Number, signer);
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

        private Snapshot GetSnapshot(ulong number, Keccak hash)
        {
            // Search for a snapshot in memory or on disk for checkpoints
            List<BlockHeader> headers = new List<BlockHeader>();
            Snapshot snap = null;
            while (snap == null)
            {
                // If an in-memory snapshot was found, use that
                Snapshot s = _recents.Get(hash);
                if (s != null)
                {
                    snap = s;
                    break;
                }

                // If an on-disk checkpoint snapshot can be found, use that
                if (number % CheckpointInterval == 0)
                {
                    s = Snapshot.LoadSnapshot(_config, _signatures, _blocksDb, hash.Bytes);
                    if (s != null)
                    {
                        snap = s;
                        break;
                    }
                }

                // If we're at an checkpoint block, make a snapshot if it's known
                if (number == 0 || (number % _config.Epoch == 0 && _blockTree.FindHeader(number - 1) == null))
                {
                    BlockHeader checkpoint = _blockTree.FindHeader(number);
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

                        snap = Snapshot.NewSnapshot(_config, _signatures, number, blockHash.Bytes, signers);
                        snap.Store(_blocksDb);
                        break;
                    }
                }

                // No snapshot for this header, gather the header and move backward
                BlockHeader header;
                // No explicit parents (or no more left), reach out to the database
                header = _blockTree.FindHeader(hash);
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

            snap = snap.Apply(headers);

            Keccak snapHash = new Keccak(snap.Hash);

            _recents.Set(snapHash, snap);
            // If we've generated a new checkpoint snapshot, save to disk
            if ((uint) snap.Number % CheckpointInterval == 0 && headers.Count > 0)
            {
                snap.Store(_blocksDb);
            }

            return snap;
        }

        private void Prepare(BlockHeader header)
        {
            // If the block isn't a checkpoint, cast a random vote (good enough for now)
            ulong number = (ulong) header.Number;
            // Assemble the voting snapshot to check which votes make sense
            Snapshot snap = GetSnapshot(number - 1, header.ParentHash);
            if ((uint) number % _config.Epoch != 0)
            {
                // Gather all the proposals that make sense voting on
                List<Address> addresses = new List<Address>();
                foreach (var proposal in _proposals)
                {
                    Address address = proposal.Key;
                    bool authorize = proposal.Value;
                    if (snap.ValidVote(address, authorize))
                    {
                        addresses.Append(address);
                    }
                }

                // If there's pending proposals, cast a vote on them
                if (addresses.Count > 0)
                {
                    Random rnd = new Random();
                    header.Beneficiary = addresses[rnd.Next(addresses.Count)];
                    if (_proposals[header.Beneficiary])
                    {
                        header.Nonce = NonceAuthVote;
                    }
                    else
                    {
                        header.Nonce = NonceDropVote;
                    }
                }
            }

            // Set the correct difficulty
            header.Difficulty = CalcDifficulty(snap, _key.Address);
            // Ensure the extra data has all it's components
            if (header.ExtraData.Length < ExtraVanity)
            {
                for (int i = 0; i < ExtraVanity - header.ExtraData.Length; i++)
                {
                    header.ExtraData.Append((byte) 0);
                }
            }

            header.ExtraData = header.ExtraData.Take(ExtraVanity).ToArray();

            if (number % _config.Epoch == 0)
            {
                foreach (Address signer in snap.Signers)
                {
                    foreach (byte addressByte in signer.Bytes)
                    {
                        header.ExtraData.Append(addressByte);
                    }
                }
            }

            var extraSeal = new byte[ExtraSeal];
            for (int i = 0; i < ExtraSeal; i++)
            {
                header.ExtraData.Append((byte) 0);
            }

            // Mix digest is reserved for now, set to empty
            // Ensure the timestamp has the correct delay
            BlockHeader parent = _blockTree.FindHeader(header.ParentHash);
            if (parent == null)
            {
                throw new InvalidOperationException("Unknown ancestor");
            }

            header.Timestamp = parent.Timestamp + _config.Period;
            var currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (header.Timestamp < currentTimestamp)
            {
                header.Timestamp = new UInt256(currentTimestamp);
            }
        }

        private Block Finalize(StateProvider state, BlockHeader header, Transaction[] txs, BlockHeader[] uncles, TransactionReceipt[] receipts)
        {
            // No block rewards in PoA, so the state remains as is and uncles are dropped
            header.StateRoot = state.StateRoot;
            header.OmmersHash = BlockHeader.CalculateHash((BlockHeader) null);
            // Assemble and return the final block for sealing
            return new Block(header, txs, null);
        }

        private UInt256 CalcDifficulty(uint time, BlockHeader parent)
        {
            ulong parentNumber = (ulong) parent.Number;
            Snapshot snap = GetSnapshot(parentNumber, BlockHeader.CalculateHash(parent));
            return CalcDifficulty(snap, _key.Address);
        }

        private UInt256 CalcDifficulty(Snapshot snapshot, Address signer)
        {
            if (snapshot.Inturn(snapshot.Number + 1, signer))
            {
                return new UInt256(DiffInTurn);
            }

            return new UInt256(DiffNoTurn);
        }

        private bool VerifyCascadingFields(BlockHeader header)
        {
            ulong number = (ulong) header.Number;
            // Ensure that the block's timestamp isn't too close to it's parent
            BlockHeader parent = _blockTree.FindHeader(header.ParentHash);
            if (parent.Timestamp + _config.Period > header.Timestamp)
            {
                _logger.Warn($"Incorrect block timestamp ({header.Timestamp}) - should have big enough different with parent");
                return false;
            }

            // Retrieve the snapshot needed to verify this header and cache it
            Snapshot snap = GetSnapshot(number - 1, header.ParentHash);

            // If the block is a checkpoint block, verify the signer list
            if (number % _config.Epoch == 0)
            {
                var signersBytes = new byte[snap.Signers.Count * AddressLength];
                var signers = snap.GetSigners();
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