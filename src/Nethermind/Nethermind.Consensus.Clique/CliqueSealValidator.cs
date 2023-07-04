// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Consensus.Clique
{
    internal class CliqueSealValidator : ISealValidator
    {
        private readonly ICliqueConfig _cliqueConfig;
        private readonly ISnapshotManager _snapshotManager;
        private ILogger _logger;

        public CliqueSealValidator(ICliqueConfig cliqueConfig, ISnapshotManager snapshotManager, ILogManager logManager)
        {
            _cliqueConfig = cliqueConfig ?? throw new ArgumentNullException(nameof(cliqueConfig));
            _snapshotManager = snapshotManager ?? throw new ArgumentNullException(nameof(snapshotManager));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public bool ValidateParams(BlockHeader parent, BlockHeader header, bool isUncle = false)
        {
            long number = header.Number;
            // Retrieve the snapshot needed to validate this header and cache it
            Snapshot snapshot = _snapshotManager.GetOrCreateSnapshot(number - 1, header.ParentHash);
            // Resolve the authorization key and check against signers
            header.Author ??= _snapshotManager.GetBlockSealer(header);
            Address signer = header.Author;
            if (!snapshot.Signers.ContainsKey(signer))
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block signer {signer} - not authorized to sign a block");
                return false;
            }

            if (_snapshotManager.HasSignedRecently(snapshot, number, signer))
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block signer {signer} - the signer is among recents");
                return false;
            }

            // Ensure that the difficulty corresponds to the turn-ness of the signer
            bool inTurn = _snapshotManager.IsInTurn(snapshot, header.Number, signer);
            if (inTurn && header.Difficulty != Clique.DifficultyInTurn)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block difficulty {header.Difficulty} - should be in-turn {Clique.DifficultyInTurn}");
                return false;
            }

            if (!inTurn && header.Difficulty != Clique.DifficultyNoTurn)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block difficulty {header.Difficulty} - should be no-turn {Clique.DifficultyNoTurn}");
                return false;
            }

            bool isEpochTransition = IsEpochTransition(header.Number);
            // Checkpoint blocks need to enforce zero beneficiary
            if (isEpochTransition && header.Beneficiary != Address.Zero)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block beneficiary ({header.Beneficiary}) - should be empty on checkpoint");
                return false;
            }

            if (isEpochTransition && header.Nonce != Clique.NonceDropVote)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block nonce ({header.Nonce}) - should be zeroes on checkpoints");
                return false;
            }

            // Ensure that the extra-data contains a signer list on checkpoint, but none otherwise
            int singersBytes = header.ExtraData.Length - Clique.ExtraVanityLength - Clique.ExtraSealLength;
            if (!isEpochTransition && singersBytes != 0)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block extra-data ({header.ExtraData}) - should be empty on non-checkpoints");
                return false;
            }

            if (isEpochTransition && singersBytes % Address.ByteLength != 0)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block nonce ({header.ExtraData}) - should contain a list of signers on checkpoints");
                return false;
            }

            // Nonce must be 0x00..0 or 0xff..f, zeroes enforced on checkpoints
            if (header.Nonce != Clique.NonceAuthVote && header.Nonce != Clique.NonceDropVote)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block nonce ({header.Nonce})");
                return false;
            }

            if (header.ExtraData.Length < Clique.ExtraVanityLength)
            {
                if (_logger.IsWarn) _logger.Warn("Invalid block extra data length - missing vanity");
                return false;
            }

            if (header.ExtraData.Length < Clique.ExtraVanityLength + Clique.ExtraSealLength)
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
            if (header.UnclesHash != Keccak.OfAnEmptySequenceRlp)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block uncles hash ({header.UnclesHash}) - uncles are meaningless in Clique");
                return false;
            }

            if (header.Difficulty != Clique.DifficultyInTurn && header.Difficulty != Clique.DifficultyNoTurn)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block difficulty ({header.Difficulty}) - should be {Clique.DifficultyInTurn} or {Clique.DifficultyNoTurn}");
                return false;
            }

            return ValidateCascadingFields(parent, header);
        }

        public bool ValidateSeal(BlockHeader header, bool force)
        {
            header.Author ??= _snapshotManager.GetBlockSealer(header);
            return header.Author is not null;
        }

        private bool IsEpochTransition(long number)
        {
            return (ulong)number % _cliqueConfig.Epoch == 0;
        }

        private bool ValidateCascadingFields(BlockHeader parent, BlockHeader header)
        {
            long number = header.Number;
            if (parent.Timestamp + _cliqueConfig.BlockPeriod > header.Timestamp)
            {
                if (_logger.IsWarn) _logger.Warn($"Incorrect block timestamp ({header.Timestamp}) - should have big enough difference with parent");
                return false;
            }

            // Retrieve the snapshot needed to validate this header and cache it
            Snapshot snapshot = _snapshotManager.GetOrCreateSnapshot(number - 1, header.ParentHash);

            // If the block is a checkpoint block, validate the signer list
            if (IsEpochTransition(number))
            {
                byte[] signersBytes = new byte[snapshot.Signers.Count * Address.ByteLength];
                int signerIndex = 0;
                foreach (Address signer in snapshot.Signers.Keys) Array.Copy(signer.Bytes, 0, signersBytes, signerIndex++ * Address.ByteLength, Address.ByteLength);

                int extraSuffix = header.ExtraData.Length - Clique.ExtraSealLength - Clique.ExtraVanityLength;
                if (!header.ExtraData.AsSpan(Clique.ExtraVanityLength, extraSuffix).SequenceEqual(signersBytes))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
