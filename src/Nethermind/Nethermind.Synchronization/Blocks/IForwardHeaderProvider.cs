// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Synchronization.Blocks;

public interface IForwardHeaderProvider
{
    Task<IOwnedReadOnlyList<BlockHeader?>?> GetBlockHeaders(int skipLastN, int maxHeaders, CancellationToken cancellation);
    void OnSuggestBlock(BlockTreeSuggestOptions blockTreeSuggestOptions, Block currentBlock, AddBlockResult addResult);
}
