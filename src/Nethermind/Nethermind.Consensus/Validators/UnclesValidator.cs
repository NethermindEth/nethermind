// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Consensus.Validators
{
    public class UnclesValidator(IBlockTree? blockTree, IHeaderValidator? headerValidator, ILogManager? logManager) : IUnclesValidator
    {
        private const int RelationshipLevel = 6;
        private const int MaxAncestorLevelsToCheckForDuplicates = 5;
        private const int MaxAncestorsDepth = RelationshipLevel;

        private readonly IBlockTree _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        private readonly IHeaderValidator _headerValidator = headerValidator ?? throw new ArgumentNullException(nameof(headerValidator));
        private readonly ILogger _logger = logManager?.GetClassLogger<UnclesValidator>() ?? throw new ArgumentNullException(nameof(logManager));

        [InlineArray(MaxAncestorsDepth)]
        private struct AncestorsBuffer
        {
            private BlockHeader _element0;
        }

        public bool Validate(BlockHeader header, BlockHeader[] uncles)
        {
            if (uncles.Length > 2)
            {
                _logger.Info($"Invalid block ({header.ToString(BlockHeader.Format.Full)}) - too many uncles");
                return false;
            }

            if (uncles.Length == 0)
            {
                return true;
            }

            if (uncles.Length == 2 && uncles[0].Hash == uncles[1].Hash)
            {
                _logger.Info($"Invalid block ({header.ToString(BlockHeader.Format.Full)}) - duplicated uncle");
                return false;
            }

            AncestorsBuffer buffer = default;
            Span<BlockHeader> ancestors = buffer;
            int ancestorsCount = 0;

            BlockHeader currentHeader = header;
            while (ancestorsCount < ancestors.Length)
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

                if (!IsKin(header, uncle, RelationshipLevel, ancestors, ancestorsCount))
                {
                    _logger.Info($"Invalid block ({header.ToString(BlockHeader.Format.Full)}) - uncle just pretending to be uncle");
                    return false;
                }

                for (int ancestorLevel = 0; ancestorLevel < ancestorsCount && ancestorLevel < MaxAncestorLevelsToCheckForDuplicates; ancestorLevel++)
                {
                    BlockHeader includedByAncestorHeader = ancestors[ancestorLevel];
                    if (includedByAncestorHeader.Hash is not { } includedByAncestorHash)
                    {
                        break;
                    }

                    Block? includedByAncestor = _blockTree.FindBlock(includedByAncestorHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
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

        private static bool IsKin(BlockHeader header, BlockHeader uncle, int relationshipLevel, ReadOnlySpan<BlockHeader> ancestors, int ancestorsCount)
        {
            int relativeDepth = Math.Min(ancestorsCount, relationshipLevel);
            ulong maxDepth = Math.Min((ulong)relativeDepth, header.Number);

            if (uncle.Number + maxDepth < header.Number)
            {
                return false;
            }

            for (int depth = 0; depth < (int)maxDepth; depth++)
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
