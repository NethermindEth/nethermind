// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Wallet;

[assembly: InternalsVisibleTo("Nethermind.Clique.Test")]

namespace Nethermind.Consensus.Clique
{
    internal class CliqueSealer : ISealer
    {
        private readonly ILogger _logger;
        private readonly ISnapshotManager _snapshotManager;
        private readonly ISigner _signer;
        private readonly ICliqueConfig _config;

        public CliqueSealer(ISigner signer, ICliqueConfig config, ISnapshotManager snapshotManager, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _snapshotManager = snapshotManager ?? throw new ArgumentNullException(nameof(snapshotManager));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _signer = signer ?? throw new ArgumentNullException(nameof(signer));

            if (config.Epoch == 0) config.Epoch = Clique.DefaultEpochLength;
        }

        public Task<Block?> SealBlock(Block processed, CancellationToken cancellationToken)
        {
            Block? sealedBlock = Seal(processed);
            if (sealedBlock is null) return Task.FromResult<Block?>(null);

            sealedBlock.Header.Hash = sealedBlock.Header.CalculateHash();
            return Task.FromResult(sealedBlock);
        }

        private Block? Seal(Block block)
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
            Signature signature = _signer.Sign(headerHash);
            // Copy signature bytes (R and S)
            byte[] signatureBytes = signature.Bytes;
            Array.Copy(signatureBytes, 0, header.ExtraData, header.ExtraData.Length - Clique.ExtraSealLength, signatureBytes.Length);
            // Copy signature's recovery id (V)
            byte recoveryId = signature.RecoveryId;
            header.ExtraData[^1] = recoveryId;

            return block;
        }

        public bool CanSeal(long blockNumber, Keccak parentHash)
        {
            Snapshot snapshot = _snapshotManager.GetOrCreateSnapshot(blockNumber - 1, parentHash);
            if (!_signer.CanSign)
            {
                if (_logger.IsTrace) _logger.Trace("Signer cannot sing any blocks");
                return false;
            }

            if (!snapshot.Signers.ContainsKey(_signer.Address))
            {
                if (_logger.IsTrace) _logger.Trace("Not on the signers list");
                return false;
            }

            if (_snapshotManager.HasSignedRecently(snapshot, blockNumber, _signer.Address))
            {
                if (_snapshotManager.HasSignedRecently(snapshot, blockNumber, _signer.Address))
                {
                    if (_logger.IsTrace) _logger.Trace("Signed recently");
                    return false;
                }
            }

            // If we're amongst the recent signers, wait for the next block
            if (_snapshotManager.HasSignedRecently(snapshot, blockNumber, _signer.Address))
            {
                if (_logger.IsTrace) _logger.Trace("Signed recently");
                return false;
            }

            return true;
        }

        public Address Address => _signer.Address;
    }
}
