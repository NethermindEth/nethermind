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
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Mining;
using Nethermind.Store;

[assembly: InternalsVisibleTo("Nethermind.Clique.Test")]

namespace Nethermind.Clique
{
    public class CliqueSealEngine : ISealEngine
    {
        private readonly ILogger _logger;

        public CliqueSealEngine(CliqueConfig config, IEthereumSigner signer, PrivateKey key, IDb blocksDb, BlockTree blockTree, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _blocksDb = blocksDb ?? throw new ArgumentNullException(nameof(blocksDb));
            _signer = signer ?? throw new ArgumentNullException(nameof(signer));
            _blockTree = blockTree ?? throw new ArgumentNullException();
            _key = key ?? throw new ArgumentNullException(nameof(key));

            if (config.Epoch == 0)
            {
                config.Epoch = DefaultEpochLength;
            }
        }

        public BigInteger MinGasPrice { get; set; } = 0;

        public async Task<Block> MineAsync(Block processed, CancellationToken cancellationToken)
        {
            Block block = await MineAsync(cancellationToken, processed, null).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.Error($"{nameof(MineAsync)} failed", t.Exception);
                    return null;
                }

                return t.Result;
            }, cancellationToken);

            if (block == null)
            {
                throw new SealEngineException($"{nameof(MineAsync)} failed");
            }

            return block;
        }

        public bool IsMining { get; set; }

        private async Task<Block> MineAsync(CancellationToken cancellationToken, Block processed, ulong? startNonce)
        {
            if (processed.Header.TransactionsRoot == null ||
                processed.Header.StateRoot == null ||
                processed.Header.ReceiptsRoot == null ||
                processed.Header.OmmersHash == null ||
                processed.Header.Bloom == null ||
                processed.Header.ExtraData == null)
            {
                throw new InvalidOperationException($"Requested to mine an invalid block {processed.Header}");
            }

            Task<Block> miningTask = Task.Factory.StartNew(() => Mine(processed), cancellationToken);
            await miningTask.ContinueWith(
                t =>
                {
                    if (t.IsCompleted)
                    {
                        t.Result.Header.Hash = BlockHeader.CalculateHash(t.Result.Header);
                    }
                }, cancellationToken, TaskContinuationOptions.NotOnFaulted, TaskScheduler.Default);

            return await miningTask;
        }

        private const int CheckpointInterval = 1024;
        private const int InMemorySnapshots = 128;
        internal const int InMemorySignatures = 4096;
        private const int WiggleTime = 500;

        private const int DefaultEpochLength = 30000;
        internal const int ExtraVanityLength = 32;
        internal const int ExtraSealLength = 65;
        public const ulong NonceAuthVote = UInt64.MaxValue;
        public const ulong NonceDropVote = 0UL;
        internal const int DifficultyInTurn = 2;
        internal const int DifficultyNoTurn = 1;

        private const int AddressLength = 20;

        private readonly BlockTree _blockTree;
        private CliqueConfig _config;
        private IEthereumSigner _signer;
        private PrivateKey _key;
        private IDb _blocksDb;

        private LruCache<Keccak, Snapshot> _recent = new LruCache<Keccak, Snapshot>(InMemorySnapshots);
        private LruCache<Keccak, Address> _signatures = new LruCache<Keccak, Address>(InMemorySignatures);

        public Address GetBlockSealer(BlockHeader header)
        {
            int extraSeal = 65;
            Address address = _signatures?.Get(header.Hash);
            if (address != null)
            {
                return address;
            }

            // Retrieve the signature from the header extra-data
            if (header.ExtraData.Length < extraSeal)
            {
                return null;
            }

            byte[] signatureBytes = header.ExtraData.Slice(header.ExtraData.Length - extraSeal, extraSeal);
            Signature signature = new Signature(signatureBytes);
            signature.V += 27;
            Keccak message = header.HashCliqueHeader();
            address = _signer.RecoverAddress(signature, message);
            _signatures?.Set(header.Hash, address);
            return address;
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
            if (_config.BlockPeriod == 0 && block.Transactions.Length == 0)
            {
                return null;
            }

            // Bail out if we're unauthorized to sign a block
            Snapshot snapshot = GetOrCreateSnapshot(number - 1, header.ParentHash);
            if (!snapshot.Signers.ContainsKey(_key.Address))
            {
                throw new InvalidOperationException("Not authorized to sign a block");
            }

            // If we're amongst the recent signers, wait for the next block
            if (snapshot.HasSignedRecently(number, _key.Address))
            {
                return null;
            }

            // Sweet, the protocol permits us to sign the block, wait for our time
            long currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long delay = (long) header.Timestamp - currentTimestamp;
            if (header.Difficulty == DifficultyNoTurn)
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
            Array.Copy(signatureBytes, 0, header.ExtraData, header.ExtraData.Length - ExtraSealLength, signatureBytes.Length);
            // Copy signature's recovery id (V)
            byte recoveryId = signature.RecoveryId;
            header.ExtraData[header.ExtraData.Length - 1] = recoveryId;

            // Wait until sealing is terminated or delay timeout.
            System.Threading.Thread.Sleep((int) delay);

            return block;
        }

        public bool ValidateParams(Block parent, BlockHeader header)
        {
            bool isEpochTransition = IsEpochTransition(header.Number);
            // Checkpoint blocks need to enforce zero beneficiary
            if (isEpochTransition && header.Beneficiary != Address.Zero)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block beneficiary ({header.Beneficiary}) - should be empty on checkpoint");
                return false;
            }

            if (isEpochTransition && header.Nonce != NonceDropVote)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block nonce ({header.Nonce}) - should be zeroes on checkpoints");
                return false;
            }

            // Ensure that the extra-data contains a signer list on checkpoint, but none otherwise
            int singersBytes = header.ExtraData.Length - ExtraVanityLength - ExtraSealLength;
            if (!isEpochTransition && singersBytes != 0)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block extra-data ({header.ExtraData}) - should be empty on non-checkpoints");
                return false;
            }

            if (isEpochTransition && singersBytes % AddressLength != 0)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block nonce ({header.ExtraData}) - should contain a list of signers on checkpoints");
                return false;
            }

            // Nonce must be 0x00..0 or 0xff..f, zeroes enforced on checkpoints
            if (header.Nonce != NonceAuthVote && header.Nonce != NonceDropVote)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block nonce ({header.Nonce})");
                return false;
            }

            if (header.ExtraData.Length < ExtraVanityLength)
            {
                if (_logger.IsWarn) _logger.Warn("Invalid block extra data length - missing vanity");
                return false;
            }

            if (header.ExtraData.Length < ExtraVanityLength + ExtraSealLength)
            {
                if (_logger.IsWarn) _logger.Warn("Invalid block extra data length - missing seal");
                return false;
            }

            // Ensure that the mix digest is zero as we don't have fork protection currently
            if (header.MixHash != Keccak.Zero)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block mix hash ({header.MixHash}) - should be zeroes");
                return false;
            }

            // Ensure that the block doesn't contain any uncles which are meaningless in PoA
            if (header.OmmersHash != Keccak.OfAnEmptySequenceRlp)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block ommers hash ({header.OmmersHash}) - ommers are meaningless in Clique");
                return false;
            }

            if (header.Difficulty != DifficultyInTurn && header.Difficulty != DifficultyNoTurn)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block difficulty ({header.Difficulty}) - should be {DifficultyInTurn} or {DifficultyNoTurn}");
                return false;
            }

            return ValidateCascadingFields(parent, header);
        }

        public bool ValidateSeal(BlockHeader header)
        {
            UInt256 number = header.Number;
            // Retrieve the snapshot needed to validate this header and cache it
            Snapshot snapshot = GetOrCreateSnapshot(number - 1, header.ParentHash);
            // Resolve the authorization key and check against signers
            header.Author = header.Author ?? GetBlockSealer(header);
            Address signer = header.Author;
            if (!snapshot.Signers.ContainsKey(signer))
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block signer {signer} - not authorized to sign a block");
                return false;
            }

            if (snapshot.HasSignedRecently(number, signer))
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block signer {signer} - the signer is among recents");
                return false;
            }

            // Ensure that the difficulty corresponds to the turn-ness of the signer
            bool inTurn = snapshot.InTurn(header.Number, signer);
            if (inTurn && header.Difficulty != DifficultyInTurn)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block difficulty {header.Difficulty} - should be in-turn {DifficultyInTurn}");
                return false;
            }

            if (!inTurn && header.Difficulty != DifficultyNoTurn)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block difficulty {header.Difficulty} - should be no-turn {DifficultyNoTurn}");
                return false;
            }

            return true;
        }

        internal Snapshot GetOrCreateSnapshot(UInt256 number, Keccak hash)
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
                    int signersCount = (header.ExtraData.Length - ExtraVanityLength - ExtraSealLength) / AddressLength;
                    SortedList<Address, UInt256> signers = new SortedList<Address, UInt256>(signersCount, CliqueAddressComparer.Instance);
                    for (int i = 0; i < signersCount; i++)
                    {
                        Address signer = new Address(header.ExtraData.Slice(ExtraVanityLength + i * AddressLength, AddressLength));
                        signers.Add(signer, UInt256.Zero);
                    }

                    snapshot = new Snapshot(_config, _signatures, number, header.Hash, signers);
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

            snapshot = snapshot.Apply(headers);

            _recent.Set(snapshot.Hash, snapshot);
            // If we've generated a new checkpoint snapshot, save to disk
            if ((ulong) snapshot.Number % CheckpointInterval == 0 && headers.Count > 0)
            {
                snapshot.Store(_blocksDb);
            }

            return snapshot;
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
            if ((ulong) number % CheckpointInterval == 0)
            {
                Snapshot persistedSnapshot = Snapshot.LoadSnapshot(_config, _signatures, _blocksDb, hash);
                if (persistedSnapshot != null)
                {
                    return persistedSnapshot;
                }
            }

            return null;
        }

        private bool IsEpochTransition(UInt256 number)
        {
            return (ulong) number % _config.Epoch == 0;
        }

        private bool ValidateCascadingFields(Block parent, BlockHeader header)
        {
            UInt256 number = header.Number;
            if (parent.Timestamp + _config.BlockPeriod > header.Timestamp)
            {
                if (_logger.IsWarn) _logger.Warn($"Incorrect block timestamp ({header.Timestamp}) - should have big enough different with parent");
                return false;
            }

            // Retrieve the snapshot needed to validate this header and cache it
            Snapshot snapshot = GetOrCreateSnapshot(number - 1, header.ParentHash);

            // If the block is a checkpoint block, validate the signer list
            if (IsEpochTransition(number))
            {
                byte[] signersBytes = new byte[snapshot.Signers.Count * AddressLength];
                int signerIndex = 0;
                foreach (Address signer in snapshot.Signers.Keys)
                {
                    Array.Copy(signer.Bytes, 0, signersBytes, signerIndex++ * AddressLength, AddressLength);
                }

                int extraSuffix = header.ExtraData.Length - ExtraSealLength;
                for (int i = 0; i < extraSuffix - ExtraVanityLength; i++)
                {
                    if (header.ExtraData[ExtraVanityLength + i] != signersBytes[i])
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}