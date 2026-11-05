// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin
{
    public class MergeHeaderValidator(
        IPoSSwitcher poSSwitcher,
        IHeaderValidator preMergeHeaderValidator,
        IBlockTree blockTree,
        ISpecProvider specProvider,
        ISealValidator sealValidator,
        ILogManager logManager)
        : HeaderValidator(blockTree, sealValidator, specProvider, logManager)
    {
        // https://eips.ethereum.org/EIPS/eip-3675#constants
        private const int MaxExtraDataBytes = 32;

        protected override bool Validate<TOrphaned>(BlockHeader header, BlockHeader? parent, bool isUncle, out string? error, bool validateHash)
        {
            error = null;
            // Archive imports omit TD; the local parent still constrains the consensus transition (EIP-3675).
            if (header.Difficulty != 0 && parent is not null)
            {
                (bool isTerminalParent, bool isPostMergeParent) = poSSwitcher.GetBlockConsensusInfo(parent);
                if (isTerminalParent || isPostMergeParent)
                {
                    error = "A proof-of-work block cannot follow a terminal or proof-of-stake parent.";
                    if (_logger.IsWarn) _logger.Warn($"Invalid block header {header.ToString(BlockHeader.Format.Short)} - {error}");
                    return false;
                }
            }

            // EIP-3675: a proof-of-stake block must follow a terminal or proof-of-stake block, i.e. a parent at or above
            // TTD (any parent when TTD is zero). The header's own TD is not always known when it is validated, so the
            // parent's known TD decides.
            if (header.Difficulty == 0 && parent is not null
                && poSSwitcher.TerminalTotalDifficulty is { IsZero: false } terminalTotalDifficulty
                && GetKnownTotalDifficulty(parent) is { } parentTotalDifficulty
                && parentTotalDifficulty < terminalTotalDifficulty)
            {
                error = "A proof-of-stake block cannot follow a non-terminal proof-of-work parent.";
                if (_logger.IsWarn) _logger.Warn($"Invalid block header {header.ToString(BlockHeader.Format.Short)} - {error}");
                return false;
            }

            return poSSwitcher.IsPostMerge(header)
                ? ValidateTheMergeChecks(header) && base.Validate<TOrphaned>(header, parent, isUncle, out error, validateHash)
                : ValidatePoWTotalDifficulty(header, ref error) && (
                    typeof(TOrphaned) == typeof(OnFlag)
                        ? preMergeHeaderValidator.ValidateOrphaned(header, out error)
                        : preMergeHeaderValidator.Validate(header, parent!, isUncle, out error, validateHash)
                );
        }

        protected override bool ValidateTotalDifficulty(BlockHeader header, BlockHeader parent, ref string? error) =>
            poSSwitcher.IsPostMerge(header) || base.ValidateTotalDifficulty(header, parent, ref error);

        /// <summary>
        /// Returns the total difficulty of <paramref name="header"/> from the header itself, or else from its stored chain
        /// level; <c>null</c> when neither knows it.
        /// </summary>
        /// <remarks>Zero counts as unknown: headers can be stored before their total difficulty is computed.</remarks>
        private UInt256? GetKnownTotalDifficulty(BlockHeader header)
        {
            if (header.TotalDifficulty is { IsZero: false } totalDifficulty) return totalDifficulty;
            if (header.Hash is null) return null;
            UInt256? stored = _blockTree.GetInfo(header.Number, header.Hash).Info?.TotalDifficulty;
            return stored is { IsZero: false } ? stored : null;
        }

        private bool ValidatePoWTotalDifficulty(BlockHeader header, ref string? error)
        {
            if (header.Difficulty == 0)
            {
                error = "A proof-of-work block cannot have zero difficulty.";
                if (_logger.IsWarn) _logger.Warn($"Invalid block header {header.ToString(BlockHeader.Format.Short)} - zero difficulty for PoW block.");
                return false;
            }

            return true;
        }

        private bool ValidateTheMergeChecks(BlockHeader header)
        {
            (bool isTerminal, bool isPostMerge) = poSSwitcher.GetBlockConsensusInfo(header);
            bool terminalTotalDifficultyChecks = ValidateTerminalTotalDifficultyChecks(header, isTerminal);
            bool valid = !isPostMerge ||
                        ValidateHeaderField(header, header.Difficulty, UInt256.Zero, nameof(header.Difficulty))
                        && ValidateHeaderField(header, header.Nonce, 0u, nameof(header.Nonce))
                        && ValidateHeaderField(header, header.UnclesHash, Keccak.OfAnEmptySequenceRlp, nameof(header.UnclesHash));

            return terminalTotalDifficultyChecks && valid;
        }

        protected override bool ValidateExtraData(BlockHeader header, IReleaseSpec spec, bool isUncle, ref string? error)
        {
            if (poSSwitcher.IsPostMerge(header))
            {
                if (header.ExtraData.Length > MaxExtraDataBytes)
                {
                    if (_logger.IsWarn) _logger.Warn($"Invalid block header {header.ToString(BlockHeader.Format.Short)} - the {nameof(header.ExtraData)} exceeded max length of {MaxExtraDataBytes}.");
                    error = BlockErrorMessages.InvalidExtraData;
                    return false;
                }

                return true;
            }

            return base.ValidateExtraData(header, spec, isUncle, ref error);
        }

        private bool ValidateTerminalTotalDifficultyChecks(BlockHeader header, bool isTerminal)
        {
            if ((header.TotalDifficulty ?? 0) == 0 || poSSwitcher.TerminalTotalDifficulty is null)
            {
                return true;
            }

            bool isValid = true;
            bool isPostMerge = header.IsPostMerge;
            if (!isPostMerge && !isTerminal)
            {
                if (header.TotalDifficulty >= poSSwitcher.TerminalTotalDifficulty)
                {
                    isValid = false;
                }
            }
            else if (header.TotalDifficulty < poSSwitcher.TerminalTotalDifficulty)
            {
                isValid = false;
            }

            if (!isValid)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header {header.ToString(BlockHeader.Format.Short)} - total difficulty is incorrect because of TTD, TerminalTotalDifficulty: {poSSwitcher.TerminalTotalDifficulty}, IsPostMerge {header.IsPostMerge} IsTerminalBlock: {isTerminal}");
            }

            return isValid;
        }

        private bool ValidateHeaderField<T>(BlockHeader header, T value, T expected, string name)
        {
            if (!Equals(value, expected))
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header {header.ToString(BlockHeader.Format.Full)} - the {name} is incorrect expected {expected}, got {value}. TransitionFinished: {poSSwitcher.TransitionFinished}, TTD: {_specProvider.TerminalTotalDifficulty}, IsTerminal: {header.IsTerminalBlock(_specProvider)}");
                return false;
            }

            return true;
        }
    }
}
