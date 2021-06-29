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
        private Block? _lastHeadBlock;
        private UInt256? _defaultGasPrice;
        private UInt256? _ignoreUnder;
        private readonly IBlockFinder _blockFinder;
        private readonly int _blocksToGoBack;
        private readonly int _txThreshold;
        public const int NoHeadBlockChangeErrorCode = 7;
        private const int Percentile = 20;

        public GasPriceOracle(IBlockFinder blockFinder, UInt256? ignoreUnder = null, int blocksToGoBack = 20)
        {
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
            _blocksToGoBack = blocksToGoBack;
            _txThreshold = SetTxThreshold();
            _ignoreUnder = ignoreUnder ?? UInt256.Zero;
        }

        private int SetTxThreshold()
        {
            return _blocksToGoBack * 2;
        }

        private bool AddTxFromBlockToSet(Block block, ref SortedSet<UInt256> sortedSet)
        {
            int length = block.Transactions.Length;
            int added = 0;
            
            if (length > 0)
            {
                for (int index = 0; index < length; index++)
                {
                    Transaction transaction = GetTransactionFromBlockAtIndex(block, index);
                    if (!transaction.IsEip1559 && transaction.GasPrice >= _ignoreUnder) //how should i set to be null?
                    {
                        sortedSet.Add(transaction.GasPrice);
                        added++;
                    }
                }

                if (added == 0)
                {
                    sortedSet.Add((UInt256) _defaultGasPrice!);
                }
                
                return MoreThanOneTransactionAdded(sortedSet, added);
            }
            else
            {
                sortedSet.Add((UInt256) _defaultGasPrice!);
                return false;
            }
        }

        private static Transaction GetTransactionFromBlockAtIndex(Block block, int index)
        {
            return block.Transactions[index];
        }

        private static bool MoreThanOneTransactionAdded(SortedSet<UInt256> sortedSet, int added)
        {
            return added > 1;
        }

        private void SetDefaultGasPrice(long headBlockNumber)
        {
            Transaction[] transactions;
            Transaction[] notEip1559Txs;
            int blocksToCheck = 8;
            
            while (headBlockNumber >= 0 && blocksToCheck-- > 0) //TEST
            {
                transactions = _blockFinder.FindBlock(headBlockNumber)!.Transactions;
                notEip1559Txs = transactions.Where(t => !t.IsEip1559).ToArray();
                if (TransactionsExist(notEip1559Txs))
                {
                    _defaultGasPrice = notEip1559Txs[^1].GasPrice; //are tx in order of time or price
                    return;
                }

                headBlockNumber--;
            }
            _defaultGasPrice = 1; 
        }

        private static bool TransactionsExist(Transaction[] transactions)
        {
            return transactions.Length > 0;
        }

        private SortedSet<UInt256> AddingTxPrices(SortedSet<UInt256> gasPrices)
        {
            long currentBlockNumber = GetHeadBlock()!.Number;
            int blocksToGoBack = _blocksToGoBack;
            while (MoreBlocksToGoBack(blocksToGoBack) && CurrentBlockNumberIsValid(currentBlockNumber)) 
            {
                Block? block = _blockFinder.FindBlock(currentBlockNumber);
                if (BlockExists(block))
                {
                    bool moreThanOneTxAdded = AddTxFromBlockToSet(block, ref gasPrices);
                    if (moreThanOneTxAdded || BonusBlockLimitReached(gasPrices, blocksToGoBack))
                    {
                        blocksToGoBack--;
                    }
                }
                currentBlockNumber--;
            }

            return gasPrices;
        }

        private Block? GetHeadBlock()
        {
            return _blockFinder.FindHeadBlock();
        }

        private bool BonusBlockLimitReached(SortedSet<UInt256> gasPrices, int blocksToGoBack)
        {
            return gasPrices.Count + blocksToGoBack >= _txThreshold;
        }

        private static bool BlockExists(Block? foundBlock)
        {
            return foundBlock != null;
        }

        private static bool CurrentBlockNumberIsValid(long currBlockNumber)
        {
            return currBlockNumber > -1;
        }

        private static bool MoreBlocksToGoBack(long blocksToGoBack)
        {
            return blocksToGoBack > 0;
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
            
            if (DefaultPriceExists() && LastHeadBlockExists() && LastHeadIsSameAsCurrentHead(headBlock))
            {
                resultWrapper = ResultWrapper<UInt256?>.Success(_defaultGasPrice);
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

        private bool DefaultPriceExists()
        {
            return _defaultGasPrice != null;
        }

        public ResultWrapper<UInt256?> GasPriceEstimate()
        {
            Block? headBlock = _blockFinder.FindHeadBlock();
            Block? genesisBlock = _blockFinder.FindGenesisBlock();
            UInt256? gasPriceEstimate = null;
            ResultWrapper<UInt256?> resultWrapper;
            
            _lastHeadBlock = headBlock;
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

            SetDefaultGasPrice(headBlock!.Number);
            SortedSet<UInt256> gasPricesWithDuplicates = new(GetDuplicateComparer());
            
            gasPricesWithDuplicates = AddingTxPrices(gasPricesWithDuplicates);

            
            int finalIndex = (int) Math.Round(((gasPricesWithDuplicates.Count - 1) * ((float) Percentile / 100)));
            foreach (UInt256 gasPrice in gasPricesWithDuplicates.Where(_ => finalIndex-- <= 0))
            {
                gasPriceEstimate = gasPrice;
                break;
            }
            return ResultWrapper<UInt256?>.Success((UInt256) gasPriceEstimate!);
        }

        private static bool ResultWrapperWasSuccessful(ResultWrapper<UInt256?> resultWrapper)
        {
            return resultWrapper.Result == Result.Success;
        }
        
        private static bool ResultWrapperWasNotSuccessful(ResultWrapper<UInt256?> resultWrapper)
        {
            return resultWrapper.Result != Result.Success;
        }
        
        private static Comparer<UInt256> GetDuplicateComparer()
        {
            Comparer<UInt256> comparerForDuplicates = Comparer<UInt256>.Create(((a, b) =>
            {
                int res = a.CompareTo(b);
                return res == 0 ? 1 : res;
            }));
            return comparerForDuplicates;
        }
    }
}
