// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Core;
using Nethermind.State;

namespace Nethermind.JsonRpc.Modules.Admin;

public class PruningTrieStateAdminRpcModule(
    ManualPruningTrigger manualPruningTrigger,
    IBlockTree blockTree,
    IStateReader stateReader,
    IVerifyTrieStarter verifyTrieStarter
) : IPruningTrieStateAdminRpcModule
{
    public ResultWrapper<PruningStatus> admin_prune()
    {
        return ResultWrapper<PruningStatus>.Success(manualPruningTrigger.Trigger());
    }

    public ResultWrapper<string> admin_verifyTrie(BlockParameter block)
    {
        BlockHeader? header = blockTree.FindHeader(block);
        if (header is null)
        {
            return ResultWrapper<string>.Fail("Unable to find block. Unable to know state root to verify.");
        }

        if (!stateReader.HasStateForBlock(header))
        {
            return ResultWrapper<string>.Fail("Unable to start verify trie. State for block missing.");
        }

        if (!verifyTrieStarter.TryStartVerifyTrie(header))
        {
            return ResultWrapper<string>.Fail("Unable to start verify trie. Verify trie already running.");
        }

        return ResultWrapper<string>.Success("Starting.");
    }
}
