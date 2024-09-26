// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Flashbots.Modules.Flashbots;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Flasbots.Test;

public partial class FlashbotsModuleTests
{
    
    public async Task TestValidateBuilderSubmissionV3 ()
    {
        using MergeTestBlockChain chain = await CreateBlockChain();
        ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv = chain.CreateReadOnlyTxProcessingEnv();
        IFlashbotsRpcModule rpc = CreateFlashbotsModule(chain, readOnlyTxProcessingEnv);
        BlockHeader currentHeader = chain.BlockTree.Head.Header;
        IWorldState State = chain.State;

        UInt256 nonce = State.GetNonce(TestKeysAndAddress.TestAddr);

        Transaction tx1 = new Transaction(
            
        );
    }
}
