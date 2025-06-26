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
    private const string MissingBlockError = "Unable to find block. Unable to know state root to verify.";
    private const string MissingStateError = "Unable to start verify trie. State for block missing.";
    private const string AlreadyRunningError = "Unable to start verify trie. Verify trie already running.";

    public ResultWrapper<PruningStatus> admin_prune()
    {
        return ResultWrapper<PruningStatus>.Success(manualPruningTrigger.Trigger());
    }

    public ResultWrapper<string> admin_verifyTrie(BlockParameter block)
    {
        BlockHeader? header = blockTree.FindHeader(block);
        if (header is null)
        {
            return ResultWrapper<string>.Fail(MissingBlockError, ErrorCodes.ResourceNotFound);
        }

        if (!stateReader.HasStateForBlock(header))
        {
            return ResultWrapper<string>.Fail(MissingStateError, ErrorCodes.ResourceNotFound);
        }

        if (!verifyTrieStarter.TryStartVerifyTrie(header))
        {
            return ResultWrapper<string>.Fail(AlreadyRunningError, ErrorCodes.ClientLimitExceededError);
        }

        return ResultWrapper<string>.Success("Starting.");
    }
}
