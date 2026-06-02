// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Consensus.Validators
{
    public class UnclesValidator(IBlockTree? blockTree, IHeaderValidator? headerValidator, ILogManager? logManager) : IUnclesValidator
    {
        private readonly IBlockTree _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        private readonly IHeaderValidator _headerValidator = headerValidator ?? throw new ArgumentNullException(nameof(headerValidator));
        private readonly ILogger _logger = logManager?.GetClassLogger<UnclesValidator>() ?? throw new ArgumentNullException(nameof(logManager));

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
            const int maxAncestorsDepth = relationshipLevel;

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

        private static bool IsKin(BlockHeader header, BlockHeader uncle, int relationshipLevel, BlockHeader[] ancestors, int ancestorsCount)
        {
            int maxDepth = Math.Min(Math.Min(ancestorsCount, relationshipLevel), (int)header.Number);

            if (uncle.Number < header.Number - maxDepth)
            {
                return false;
            }

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
