using System;
using System.Collections.Generic;
using System.Linq;
using Jint.Native;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class GasPriceEstimator
    {
        private UInt256? _lastPrice = null; //will this be okay if it is static?
        private Block? _lastHeadBlock = null;
        private readonly IBlockFinder _blockFinder;
        public GasPriceEstimator(IBlockFinder blockFinder)
        {
            _blockFinder = blockFinder;
        }
        private bool addTxFromBlockToSet(Block block, ref SortedSet<UInt256> sortedSet, UInt256 finalPrice,
            UInt256? ignoreUnder = null, long? maxCount = null)
        {
            ignoreUnder ??= UInt256.Zero;
            int length = block.Transactions.Length;
            int added = 0;
            if (length > 0)
            {
                for (int index = 0; index < length && (maxCount == null || sortedSet.Count < maxCount); index++)
                {
                    Transaction transaction = block.Transactions[index];
                    if (!transaction.IsEip1559 && transaction.GasPrice >= ignoreUnder)
                    {
                        sortedSet.Add(transaction.GasPrice);
                        added++;
                    }
                }

                switch (added)
                {
                    case 0:
                        sortedSet.Add(finalPrice);
                        return (false);
                    case 1:
                        return (false);
                    default:
                        return true;
                }
            }
            else
            {
                sortedSet.Add(finalPrice);
                return false;
            }
        }

        private UInt256? latestGasPrice(long headBlockNumber, long genesisBlockNumber)
        {
            int blocksToCheck = 8;
            while (headBlockNumber >= genesisBlockNumber && blocksToCheck-- > 0) //TEST
            {
                Transaction[] transactions = _blockFinder.FindBlock(headBlockNumber)!.Transactions
                    .Where(t => !t.IsEip1559).ToArray();
                if (transactions.Length > 0)
                {
                    return transactions[^1].GasPrice; //are tx in order of time or price
                }

                headBlockNumber--;
            }

            return 1; //do we want to throw an error if there are no transactions? Do we want to set lastPrice to 1 when we cannot find latestPrice in tx?
        }
        
        private SortedSet<UInt256> addingPreviousBlockTx(UInt256? ignoreUnder, long blocksToGoBack, long currBlockNumber, 
                SortedSet<UInt256> gasPrices, UInt256? gasPriceLatest, long threshold)
        //if a block has 0 tx, we add the latest gas price as its only tx
        //if a block only has 1 tx (includes blocks that had initially 0 tx), we don't count it as part of the number of blocks we go back,
        //unless after the tx is added, len(gasPrices) + blocksToGoBack >= threshold
        {
            while (blocksToGoBack > 0 && currBlockNumber > -1) //else, go back "blockNumber" valid gas prices from genesisBlock
            {
                Block? foundBlock = _blockFinder.FindBlock(currBlockNumber);
                if (foundBlock != null)
                {
                    bool result = addTxFromBlockToSet(foundBlock, ref gasPrices, (UInt256) gasPriceLatest, ignoreUnder);
                    if (result || gasPrices.Count + blocksToGoBack >= threshold)
                    {
                        blocksToGoBack--;
                    }
                }

                currBlockNumber--;
            }

            return gasPrices;
        }
        
        private SortedSet<UInt256> initializeValues(Block headBlock, Block genesisBlock, out long blocksToGoBack, out int percentile,
            out long threshold, out long currBlockNumber, out UInt256? gasPriceLatest)
        {
            Comparer<UInt256> comparer = Comparer<UInt256>.Create(((a, b) =>
            {
                int res = a.CompareTo(b);
                return res == 0 ? 1 : res;
            }));
            SortedSet<UInt256> gasPrices = new(comparer); //allows duplicates
            blocksToGoBack = 5;
            percentile = 20;
            threshold = blocksToGoBack * 2;
            currBlockNumber = headBlock!.Number;
            gasPriceLatest = latestGasPrice(headBlock!.Number, genesisBlock!.Number);
            return gasPrices;
        }
        
        private bool initalChecks(Block? headBlock, Block? genesisBlock, out ResultWrapper<UInt256?> resultWrapper)
        {
            if (headBlock == null)
            {
                resultWrapper = ResultWrapper<UInt256?>.Fail("The head block had a null value.");
                return true;
            }
            if (genesisBlock == null)
            {
                resultWrapper = ResultWrapper<UInt256?>.Fail("The genesis block had a null value.");
                return true;
            }
            if (_lastPrice != null && _lastHeadBlock != null)
            {
                if (headBlock.Hash == _lastHeadBlock.Hash)
                {
                    {
                        resultWrapper = ResultWrapper<UInt256?>.Success(this._lastPrice);
                        #if DEBUG
                            resultWrapper.ErrorCode = -1;
                        #endif
                        return true;
                    }
                }
            }

            resultWrapper = ResultWrapper<UInt256?>.Success(UInt256.Zero);
            return false;
        }
        
        public ResultWrapper<UInt256?> gasPriceEstimate(UInt256? ignoreUnder = null)
        {
            Block? headBlock = _blockFinder.FindHeadBlock();
            Block? genesisBlock = _blockFinder.FindGenesisBlock();
            if (initalChecks(headBlock, genesisBlock, out ResultWrapper<UInt256?> resultWrapper))
            {
                return resultWrapper;
            }

            SortedSet<UInt256> gasPrices = initializeValues(headBlock, genesisBlock, out long blocksToGoBack,
                out int percentile, out long threshold, out long currBlockNumber, out UInt256? gasPriceLatest);
            if (gasPriceLatest == null)
            {
                return ResultWrapper<UInt256?>.Fail("gasPriceLatest was not set properly.");
            }
            
            gasPrices = addingPreviousBlockTx(ignoreUnder, blocksToGoBack, currBlockNumber, 
                gasPrices, gasPriceLatest, threshold);

            int finalIndex = (int) Math.Round(((gasPrices.Count - 1) * ((float) percentile / 100)));
            foreach (UInt256 gasPrice in gasPrices.Where(_ => finalIndex-- <= 0))
            {
                gasPriceLatest = gasPrice;
                break;
            }

            _lastHeadBlock = headBlock;
            _lastPrice = gasPriceLatest;
            return ResultWrapper<UInt256?>.Success(gasPriceLatest);
        }

    }
}
