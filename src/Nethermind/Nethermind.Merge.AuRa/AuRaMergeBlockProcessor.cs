//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Merge.AuRa;

public class AuRaMergeBlockProcessor : AuRaBlockProcessor
{
    private readonly IPoSSwitcher _poSSwitcher;

    public AuRaMergeBlockProcessor(
        IPoSSwitcher poSSwitcher,
        ISpecProvider specProvider,
        IBlockValidator blockValidator,
        IRewardCalculator rewardCalculator,
        IBlockProcessor.IBlockTransactionsExecutor blockTransactionsExecutor,
        IStateProvider stateProvider,
        IStorageProvider storageProvider,
        IReceiptStorage receiptStorage,
        ILogManager logManager,
        IBlockTree blockTree,
        ITxFilter? txFilter = null,
        AuRaContractGasLimitOverride? gasLimitOverride = null,
        ContractRewriter? contractRewriter = null
    ) : base(
            specProvider,
            blockValidator,
            rewardCalculator,
            blockTransactionsExecutor,
            stateProvider,
            storageProvider,
            receiptStorage,
            logManager,
            blockTree,
            txFilter,
            gasLimitOverride,
            contractRewriter
        )
    {
        _poSSwitcher = poSSwitcher;
    }

    protected override TxReceipt[] ProcessBlock(Block block, IBlockTracer blockTracer, ProcessingOptions options) =>
        _poSSwitcher.IsPostMerge(block.Header) 
            ? PostMergeProcessBlock(block, blockTracer, options) 
            : base.ProcessBlock(block, blockTracer, options);
}
