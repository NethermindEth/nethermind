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
    public class GasPrice
    {
        private static UInt256? _lastPrice; //will this be okay if it is static?
        private static Block? _lastHeadBlock;
        private IBlockFinder _blockFinder;
        public GasPrice(IBlockFinder blockFinder)
        {
            _lastPrice = null;
            _lastHeadBlock = null;
            _blockFinder = blockFinder;
        }
        private bool eth_addTxFromBlockToSet(Block block, ref SortedSet<UInt256> sortedSet, UInt256 finalPrice,
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

        private UInt256? eth_latestGasPrice(long headBlockNumber, long genesisBlockNumber)
        {
            while (headBlockNumber >= genesisBlockNumber) //should this be latestBlock or headBlock?
            {
                Transaction[] transactions = _blockFinder.FindBlock(headBlockNumber)!.Transactions
                    .Where(t => !t.IsEip1559).ToArray();
                if (transactions.Length > 0)
                {
                    return transactions[^1].GasPrice;
                }

                headBlockNumber--;
            }

            return 1; //do we want to throw an error if there are no transactions? Do we want to set lastPrice to 1 when we cannot find latestPrice in tx?
        }
        public ResultWrapper<UInt256?> eth_gasPrice(UInt256? ignoreUnder = null)
        {
            Block headBlock = _blockFinder.FindHeadBlock();
            Block genesisBlock = _blockFinder.FindGenesisBlock();
            if (_lastPrice != null && _lastHeadBlock != null)
            {    
                if (headBlock == _lastHeadBlock)
                {
                    return ResultWrapper<UInt256?>.Success(_lastPrice);
                }
            }
            if (headBlock == null || genesisBlock == null)
            {
                return ResultWrapper<UInt256?>.Fail("The head block or genesis block had a null value.");
            }

            Comparer<UInt256> comparer = Comparer<UInt256>.Create(((a, b) =>
            {
                int res = a.CompareTo(b);
                return res == 0 ? 1 : res;
            }));
            SortedSet<UInt256> gasPrices = new(comparer); //allows duplicates
            long blocksToGoBack = 5;
            int percentile = 20;
            long threshold = blocksToGoBack * 2;
            long currBlockNumber = headBlock!.Number;
            long genBlockNumber = genesisBlock!.Number;
            UInt256? gasPriceLatest = eth_latestGasPrice(headBlock!.Number, genesisBlock!.Number);
            if (gasPriceLatest == null)
            {
                return ResultWrapper<UInt256?>.Fail("gasPriceLatest was not set properly.");
            }
            
            while (blocksToGoBack > 0 && currBlockNumber > genBlockNumber - 1) //else, go back "blockNumber" valid gas prices from genesisBlock
            {
                Block? foundBlock = _blockFinder.FindBlock(currBlockNumber);
                if (foundBlock != null)
                {
                    bool result = eth_addTxFromBlockToSet(foundBlock, ref gasPrices, (UInt256) gasPriceLatest, ignoreUnder);
                    if (result || gasPrices.Count + blocksToGoBack >= threshold) //if we only added one transaction, we don't reduce blocksToGoBack if second condition holds
                    {
                        blocksToGoBack--;
                    }
                }
                currBlockNumber--;
            }

            int finalIndex = (int)Math.Round(((gasPrices.Count - 1) * ((float)percentile / 100)));
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
