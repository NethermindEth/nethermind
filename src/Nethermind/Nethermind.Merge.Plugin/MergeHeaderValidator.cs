// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Consensus.Messages;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin
{
    public sealed class MergeHeaderValidator : HeaderValidator
    {
        // https://eips.ethereum.org/EIPS/eip-3675#constants
        private const int MaxExtraDataBytes = 32;

        private readonly IPoSSwitcher _poSSwitcher;
        private readonly IHeaderValidator _preMergeHeaderValidator;
        private readonly IBlockTree _blockTree;
        private readonly ISpecProvider _specProvider;

        public MergeHeaderValidator(
            IPoSSwitcher poSSwitcher,
            IHeaderValidator preMergeHeaderValidator,
            IBlockTree blockTree,
            ISpecProvider specProvider,
            ISealValidator sealValidator,
            ILogManager logManager)
            : base(blockTree, sealValidator, specProvider, logManager)
        {
            _poSSwitcher = poSSwitcher;
            _preMergeHeaderValidator = preMergeHeaderValidator;
            _blockTree = blockTree;
            _specProvider = specProvider;
        }

        public override bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle = false)
        {
            return Validate(header, parent, isUncle, out _);
        }
        public override bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle, out string? error)
        {
            error = null;
            return _poSSwitcher.IsPostMerge(header)
                ? ValidateTheMergeChecks(header) && base.Validate(header, parent, isUncle, out error)
                : ValidatePoWTotalDifficulty(header) && _preMergeHeaderValidator.Validate(header, parent, isUncle, out error);
        }

        public override bool Validate(BlockHeader header, bool isUncle, out string? error) =>
            Validate(header, _blockTree.FindParentHeader(header, BlockTreeLookupOptions.None), isUncle, out error);

        public override bool Validate(BlockHeader header, bool isUncle = false) =>
            Validate(header, _blockTree.FindParentHeader(header, BlockTreeLookupOptions.None), isUncle);

        protected override bool ValidateTotalDifficulty(BlockHeader parent, BlockHeader header, ref string? error) =>
            _poSSwitcher.IsPostMerge(header) || base.ValidateTotalDifficulty(parent, header, ref error);

        private bool ValidatePoWTotalDifficulty(BlockHeader header)
        {
            if (header.Difficulty == 0)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header {header.ToString(BlockHeader.Format.Short)} - zero difficulty for PoW block.");
                return false;
            }

            return true;
        }

        private bool ValidateTheMergeChecks(BlockHeader header)
        {
            (bool IsTerminal, bool IsPostMerge) switchInfo = _poSSwitcher.GetBlockConsensusInfo(header);
            bool terminalTotalDifficultyChecks = ValidateTerminalTotalDifficultyChecks(header, switchInfo.IsTerminal);
            bool valid = !switchInfo.IsPostMerge ||
                        ValidateHeaderField(header, header.Difficulty, UInt256.Zero, nameof(header.Difficulty))
                        && ValidateHeaderField(header, header.Nonce, 0u, nameof(header.Nonce))
                        && ValidateHeaderField(header, header.UnclesHash, Keccak.OfAnEmptySequenceRlp, nameof(header.UnclesHash));

            return terminalTotalDifficultyChecks && valid;
        }

        protected override bool ValidateExtraData(BlockHeader header, BlockHeader? parent, IReleaseSpec spec, bool isUncle, ref string? error)
        {
            if (_poSSwitcher.IsPostMerge(header))
            {
                if (header.ExtraData.Length > MaxExtraDataBytes)
                {
                    if (_logger.IsWarn) _logger.Warn($"Invalid block header {header.ToString(BlockHeader.Format.Short)} - the {nameof(header.ExtraData)} exceeded max length of {MaxExtraDataBytes}.");
                    error = BlockErrorMessages.InvalidExtraData;
                    return false;
                }

                return true;
            }

            return base.ValidateExtraData(header, parent, spec, isUncle, ref error);
        }

        private bool ValidateTerminalTotalDifficultyChecks(BlockHeader header, bool isTerminal)
        {
            if ((header.TotalDifficulty ?? 0) == 0 || _poSSwitcher.TerminalTotalDifficulty is null)
            {
                return true;
            }

            bool isValid = true;
            bool isPostMerge = header.IsPostMerge;
            if (!isPostMerge && !isTerminal)
            {
                if (header.TotalDifficulty >= _poSSwitcher.TerminalTotalDifficulty)
                {
                    isValid = false;
                }
            }
            else if (header.TotalDifficulty < _poSSwitcher.TerminalTotalDifficulty)
            {
                isValid = false;
            }

            if (!isValid)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header {header.ToString(BlockHeader.Format.Short)} - total difficulty is incorrect because of TTD, TerminalTotalDifficulty: {_poSSwitcher.TerminalTotalDifficulty}, IsPostMerge {header.IsPostMerge} IsTerminalBlock: {isTerminal}");
            }

            return isValid;
        }

        private bool ValidateHeaderField<T>(BlockHeader header, T value, T expected, string name)
        {
            if (!Equals(value, expected))
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid block header {header.ToString(BlockHeader.Format.Full)} - the {name} is incorrect expected {expected}, got {value}. TransitionFinished: {_poSSwitcher.TransitionFinished}, TTD: {_specProvider.TerminalTotalDifficulty}, IsTerminal: {header.IsTerminalBlock(_specProvider)}");
                return false;
            }

            return true;
        }
    }
}
