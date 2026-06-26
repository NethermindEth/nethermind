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
using System.Diagnostics.CodeAnalysis;

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

        protected override bool Validate<TOrphaned>(BlockHeader header, BlockHeader? parent, bool isUncle, [NotNullWhen(false)] out string? error)
        {
            error = null;
            return poSSwitcher.IsPostMerge(header)
                ? ValidateTheMergeChecks(header, ref error) && base.Validate<TOrphaned>(header, parent, isUncle, out error)
                : ValidatePoWTotalDifficulty(header, ref error) && (
                    typeof(TOrphaned) == typeof(OnFlag)
                        ? preMergeHeaderValidator.ValidateOrphaned(header, out error)
                        : preMergeHeaderValidator.Validate(header, parent!, isUncle, out error)
                );
        }

        protected override bool ValidateTotalDifficulty(BlockHeader header, BlockHeader parent, ref string? error) =>
            poSSwitcher.IsPostMerge(header) || base.ValidateTotalDifficulty(header, parent, ref error);

        private bool ValidatePoWTotalDifficulty(BlockHeader header, ref string? error)
        {
            if (header.Difficulty == 0)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header {header.ToString(BlockHeader.Format.Short)} - zero difficulty for PoW block.");
                error = BlockErrorMessages.InvalidTotalDifficulty;
                return false;
            }

            return true;
        }

        private bool ValidateTheMergeChecks(BlockHeader header, ref string? error)
        {
            (bool isTerminal, bool isPostMerge) = poSSwitcher.GetBlockConsensusInfo(header);
            bool terminalTotalDifficultyChecks = ValidateTerminalTotalDifficultyChecks(header, isTerminal, ref error);
            bool valid = !isPostMerge ||
                        ValidateHeaderField(header, header.Difficulty, UInt256.Zero, nameof(header.Difficulty), ref error)
                        && ValidateHeaderField(header, header.Nonce, 0u, nameof(header.Nonce), ref error)
                        && ValidateHeaderField(header, header.UnclesHash, Keccak.OfAnEmptySequenceRlp, nameof(header.UnclesHash), ref error);

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

        private bool ValidateTerminalTotalDifficultyChecks(BlockHeader header, bool isTerminal, ref string? error)
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
                error = BlockErrorMessages.InvalidTotalDifficulty;
            }

            return isValid;
        }

        private bool ValidateHeaderField<T>(BlockHeader header, T value, T expected, string name, ref string? error)
        {
            if (!Equals(value, expected))
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header {header.ToString(BlockHeader.Format.Full)} - the {name} is incorrect expected {expected}, got {value}. TransitionFinished: {poSSwitcher.TransitionFinished}, TTD: {_specProvider.TerminalTotalDifficulty}, IsTerminal: {header.IsTerminalBlock(_specProvider)}");
                error = BlockErrorMessages.InvalidSealParameters;
                return false;
            }

            return true;
        }
    }
}
