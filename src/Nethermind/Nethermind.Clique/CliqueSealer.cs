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
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Mining;
using Nethermind.Wallet;

[assembly: InternalsVisibleTo("Nethermind.Clique.Test")]

namespace Nethermind.Clique
{
    public class CliqueSealer : ISealer
    {
        private readonly ILogger _logger;
        private readonly Address _sealerAddress;
        private readonly ISnapshotManager _snapshotManager;
        private readonly IBasicWallet _wallet;
        private ICliqueConfig _config;

        public CliqueSealer(IBasicWallet wallet, ICliqueConfig config, ISnapshotManager snapshotManager, Address sealerAddress, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _snapshotManager = snapshotManager ?? throw new ArgumentNullException(nameof(snapshotManager));
            _sealerAddress = sealerAddress ?? throw new ArgumentNullException(nameof(sealerAddress));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));

            if (config.Epoch == 0) config.Epoch = Clique.DefaultEpochLength;
        }

        public async Task<Block> SealBlock(Block processed, CancellationToken cancellationToken)
        {
            Block sealedBlock = Seal(processed);
            if (sealedBlock == null) return null;

            sealedBlock.Hash = BlockHeader.CalculateHash(sealedBlock.Header);
            return await Task.FromResult(sealedBlock);
        }

        private Block Seal(Block block)
        {
            // Bail out if we're unauthorized to sign a block
            if (!CanSeal(block.Number, block.ParentHash))
            {
                if (_logger.IsInfo) _logger.Info($"Not authorized to seal the block {block.ToString(Block.Format.Short)}");
                return null;
            }
            
            BlockHeader header = block.Header;

            // Sealing the genesis block is not supported
            long number = header.Number;
            if (number == 0) throw new InvalidOperationException("Can't sign genesis block");

            // For 0-period chains, refuse to seal empty blocks (no reward but would spin sealing)
            if (_config.BlockPeriod == 0 && block.Transactions.Length == 0)
            {
                if (_logger.IsError) _logger.Error($"Not sealing empty block on 0-period chain {block.Number}");
                throw new InvalidOperationException("An attempt has been made to seal an empty block on a 0-period clique chain");
            }

            // Sign all the things!
            Keccak headerHash = SnapshotManager.CalculateCliqueHeaderHash(header);
            Signature signature = _wallet.Sign(headerHash, _sealerAddress);
            // Copy signature bytes (R and S)
            var signatureBytes = signature.Bytes;
            Array.Copy(signatureBytes, 0, header.ExtraData, header.ExtraData.Length - Clique.ExtraSealLength, signatureBytes.Length);
            // Copy signature's recovery id (V)
            byte recoveryId = signature.RecoveryId;
            header.ExtraData[header.ExtraData.Length - 1] = recoveryId;

            return block;
        }

        public bool CanSeal(long blockNumber, Keccak parentHash)
        {
            Snapshot snapshot = _snapshotManager.GetOrCreateSnapshot(blockNumber - 1, parentHash);
            if (!snapshot.Signers.ContainsKey(_sealerAddress))
            {
                if (_logger.IsTrace) _logger.Trace("Not on the signers list");
                return false;
            }

            if (_snapshotManager.HasSignedRecently(snapshot, blockNumber, _sealerAddress))
            {
                if (_snapshotManager.HasSignedRecently(snapshot, blockNumber, _sealerAddress))
                {
                    if (_logger.IsTrace) _logger.Trace("Signed recently");
                    return false;
                }
            }
            
            // If we're amongst the recent signers, wait for the next block
            if (_snapshotManager.HasSignedRecently(snapshot, blockNumber, _sealerAddress))
            {
                if (_logger.IsTrace) _logger.Trace("Signed recently");
                return false;
            }

            return true;
        }
    }
}