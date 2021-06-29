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
    public class GasPriceOracle : IGasPriceOracle
    {
        private UInt256? _lastPrice;
        private Block? _lastHeadBlock;
        private readonly IBlockFinder _blockFinder;
        private readonly int _blocksToGoBack;
        public const int NoHeadBlockChangeErrorCode = 7;
        private const int Percentile = 20;
        
        public GasPriceOracle(IBlockFinder blockFinder, int blocksToGoBack = 20)
        {
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
            _blocksToGoBack = blocksToGoBack;
        }
        private bool AddTxFromBlockToSet(Block block, ref SortedSet<UInt256> sortedSet, UInt256 finalPrice,
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
                    if (!transaction.IsEip1559 && transaction.GasPrice != null && transaction.GasPrice >= ignoreUnder) //how should i set to be null?
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

        private UInt256? LatestGasPrice(long headBlockNumber)
        {
            int blocksToCheck = 8;
            while (headBlockNumber >= 0 && blocksToCheck-- > 0) //TEST
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
        
        private SortedSet<UInt256> AddingPreviousBlockTx(UInt256? ignoreUnder, long blocksToGoBack, long currBlockNumber, 
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
                    bool result = AddTxFromBlockToSet(foundBlock, ref gasPrices, (UInt256) gasPriceLatest, ignoreUnder);
                    if (result || gasPrices.Count + blocksToGoBack >= threshold)
                    {
                        blocksToGoBack--;
                    }
                }

                currBlockNumber--;
            }

            return gasPrices;
        }
        
        private SortedSet<UInt256> InitializeValues(Block? headBlock, out long threshold, 
            out long currBlockNumber, out UInt256? gasPriceLatest)
        {
            Comparer<UInt256> comparerForDuplicates = Comparer<UInt256>.Create(((a, b) =>
            {
                int res = a.CompareTo(b);
                return res == 0 ? 1 : res;
            }));
            SortedSet<UInt256> gasPricesWithDuplicates = new(comparerForDuplicates);
            threshold = _blocksToGoBack * 2;
            currBlockNumber = headBlock!.Number;
            gasPriceLatest = LatestGasPrice(headBlock!.Number);
            return gasPricesWithDuplicates;
        }

        private ResultWrapper<UInt256?> HandleMissingHeadOrGenesisBlockCase(Block? headBlock, Block? genesisBlock)
        {
            if (BlockDoesNotExist(headBlock))
            {
                return ResultWrapper<UInt256?>.Fail("The head block had a null value.");
            }
            else if (BlockDoesNotExist(genesisBlock))
            {
                return ResultWrapper<UInt256?>.Fail("The genesis block had a null value.");
            }
            else
            {
                return ResultWrapper<UInt256?>.Success(UInt256.Zero);
            }
        }

        private static bool BlockDoesNotExist(Block? block)
        {
            return block == null;
        }

        private ResultWrapper<UInt256?> HandleNoHeadBlockChange(Block? headBlock)
        {
            ResultWrapper<UInt256?> resultWrapper;
            
            if (LastPriceExists() && LastHeadBlockExists() && LastHeadIsSameAsCurrentHead(headBlock))
            {
                resultWrapper = ResultWrapper<UInt256?>.Success(_lastPrice);
#if DEBUG
                resultWrapper.ErrorCode = NoHeadBlockChangeErrorCode;
#endif
                return resultWrapper;
            }
            else
            {
                return ResultWrapper<UInt256?>.Fail("");
            }
        }

        private bool LastHeadIsSameAsCurrentHead(Block? headBlock)
        {
            return headBlock!.Hash == _lastHeadBlock.Hash;
        }

        private bool LastHeadBlockExists()
        {
            return _lastHeadBlock != null;
        }

        private bool LastPriceExists()
        {
            return _lastPrice != null;
        }

        public ResultWrapper<UInt256?> GasPriceEstimate(UInt256? ignoreUnder = null)
        {
            Block? headBlock = _blockFinder.FindHeadBlock();
            Block? genesisBlock = _blockFinder.FindGenesisBlock();
            ResultWrapper<UInt256?> resultWrapper;

            resultWrapper = HandleMissingHeadOrGenesisBlockCase(headBlock, genesisBlock);
            if (ResultWrapperWasNotSuccessful(resultWrapper))
            {
                return resultWrapper;
            }

            resultWrapper = HandleNoHeadBlockChange(headBlock);
            if (ResultWrapperWasSuccessful(resultWrapper))
            {
                return resultWrapper;
            }

            SortedSet<UInt256> gasPrices = InitializeValues(headBlock, 
                out long threshold, out long currBlockNumber, out UInt256? gasPriceLatest);
            
            gasPrices = AddingPreviousBlockTx(ignoreUnder, _blocksToGoBack, currBlockNumber, 
                gasPrices, gasPriceLatest, threshold);

            int finalIndex = (int) Math.Round(((gasPrices.Count - 1) * ((float) Percentile / 100)));
            foreach (UInt256 gasPrice in gasPrices.Where(_ => finalIndex-- <= 0))
            {
                gasPriceLatest = gasPrice;
                break;
            }

            _lastHeadBlock = headBlock;
            _lastPrice = gasPriceLatest;
            return ResultWrapper<UInt256?>.Success(gasPriceLatest);
        }

        private static bool ResultWrapperWasSuccessful(ResultWrapper<UInt256?> resultWrapper)
        {
            return resultWrapper.Result == Result.Success;
        }
        
        private static bool ResultWrapperWasNotSuccessful(ResultWrapper<UInt256?> resultWrapper)
        {
            return resultWrapper.Result != Result.Success;
        }
    }
}
