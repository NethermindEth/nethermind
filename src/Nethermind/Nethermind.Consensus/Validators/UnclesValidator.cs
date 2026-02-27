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

            const int relationshipLevel = 6;
            const int maxAncestorLevelsToCheckForDuplicates = 5;
            int maxAncestorsDepth = Math.Max(relationshipLevel, maxAncestorLevelsToCheckForDuplicates);

            BlockHeader[] ancestors = new BlockHeader[maxAncestorsDepth];
            int ancestorsCount = 0;

            BlockHeader currentHeader = header;
            while (ancestorsCount < maxAncestorsDepth)
            {
                BlockHeader? parentHeader = _blockTree.FindParentHeader(currentHeader, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                if (parentHeader is null)
                {
                    break;
                }

                ancestors[ancestorsCount++] = parentHeader;
                currentHeader = parentHeader;
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

                if (!IsKin(header, uncle, relationshipLevel, ancestors, ancestorsCount))
                {
                    _logger.Info($"Invalid block ({header.ToString(BlockHeader.Format.Full)}) - uncle just pretending to be uncle");
                    return false;
                }

                for (int ancestorLevel = 0; ancestorLevel < ancestorsCount && ancestorLevel < maxAncestorLevelsToCheckForDuplicates; ancestorLevel++)
                {
                    BlockHeader includedByAncestorHeader = ancestors[ancestorLevel];
                    Block? includedByAncestor = _blockTree.FindBlock(includedByAncestorHeader.Hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                    if (includedByAncestor is null)
                    {
                        break;
                    }

                    foreach (BlockHeader ancestorUncle in includedByAncestor.Uncles)
                    {
                        if (ancestorUncle.Hash == uncle.Hash)
                        {
                            _logger.Info($"Invalid block ({header.ToString(BlockHeader.Format.Full)}) - uncles has already been included by an ancestor");
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        private bool IsKin(BlockHeader header, BlockHeader uncle, int relationshipLevel, BlockHeader[] ancestors, int ancestorsCount)
        {
            if (relationshipLevel == 0)
            {
                return false;
            }

            if (relationshipLevel > header.Number)
            {
                relationshipLevel = (int)header.Number;
            }

            if (uncle.Number < header.Number - relationshipLevel)
            {
                return false;
            }

            int maxDepth = Math.Min(relationshipLevel, ancestorsCount);

            for (int depth = 0; depth < maxDepth; depth++)
            {
                BlockHeader parent = ancestors[depth];

                if (parent.Hash == uncle.Hash)
                {
                    return false;
                }

                if (parent.ParentHash == uncle.ParentHash)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
