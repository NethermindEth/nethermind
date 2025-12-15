// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Logging;

namespace Nethermind.Consensus.Validators
{
    [Todo(Improve.Performance, "We execute the search up the tree twice - once for IsKin and once for HasAlreadyBeenIncluded")]
    public class UnclesValidator : IUnclesValidator
    {
        private readonly IBlockTree _blockTree;
        private readonly IHeaderValidator _headerValidator;
        private readonly ILogger _logger;

        public UnclesValidator(IBlockTree? blockTree, IHeaderValidator? headerValidator, ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _headerValidator = headerValidator ?? throw new ArgumentNullException(nameof(headerValidator));
        }

        public bool Validate(BlockHeader header, BlockHeader[] uncles)
        {
            if (uncles.Length > 2)
            {
                _logger.Info($"Invalid block ({header.ToString(BlockHeader.Format.Full)}) - too many uncles");
                return false;
            }

            if (uncles.Length == 2 && uncles[0].Hash == uncles[1].Hash)
            {
                _logger.Info($"Invalid block ({header.ToString(BlockHeader.Format.Full)}) - duplicated uncle");
                return false;
            }

            for (int i = 0; i < uncles.Length; i++)
            {
                BlockHeader uncle = uncles[i];
                BlockHeader? uncleParent = _blockTree.FindParentHeader(uncle, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                if (!_headerValidator.Validate(uncle, uncleParent, true, out string? err))
                {
                    _logger.Info($"Invalid block ({header.ToString(BlockHeader.Format.Full)}) - uncle's header invalid. Error: {err}");
                    return false;
                }

                (bool isKin, bool alreadyIncluded) = EvaluateKinshipAndInclusion(header, uncle);

                if (!isKin)
                {
                    _logger.Info($"Invalid block ({header.ToString(BlockHeader.Format.Full)}) - uncle just pretending to be uncle");
                    return false;
                }

                if (alreadyIncluded)
                {
                    _logger.Info($"Invalid block ({header.ToString(BlockHeader.Format.Full)}) - uncles has already been included by an ancestor");
                    return false;
                }
            }

            return true;
        }

        private (bool IsKin, bool AlreadyIncluded) EvaluateKinshipAndInclusion(BlockHeader header, BlockHeader uncle)
        {
            // Walk ancestors once to validate kinship (within six generations) and detect duplicate inclusion.
            const int maxKinDepth = 6;
            const int maxDuplicateDepth = 5;
            if (uncle.Number < header.Number - maxKinDepth)
            {
                return (false, false);
            }

            int depthLimit = (int)Math.Min(maxKinDepth, header.Number);
            bool isKin = false;
            Block? ancestor = _blockTree.FindBlock(header.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);

            for (int depth = 0; depth < depthLimit && ancestor is not null; depth++)
            {
                BlockHeader ancestorHeader = ancestor.Header;

                if (ancestorHeader.Hash == uncle.Hash)
                {
                    return (false, false);
                }

                if (ancestorHeader.ParentHash == uncle.ParentHash)
                {
                    isKin = true;
                }

                if (depth < maxDuplicateDepth && ancestor.Uncles.Any(o => o.Hash == uncle.Hash))
                {
                    return (isKin, true);
                }

                ancestor = _blockTree.FindBlock(ancestorHeader.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            }

            return (isKin, false);
        }
    }
}
