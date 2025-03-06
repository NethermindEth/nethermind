// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Optimism.Rpc;

public class OptimismEthRpcModule : EthRpcModule, IOptimismEthRpcModule
{
    private readonly IJsonRpcClient? _sequencerRpcClient;
    private readonly IEthereumEcdsa _ecdsa;
    private readonly ITxSealer _sealer;
    private readonly IOptimismSpecHelper _opSpecHelper;

    public OptimismEthRpcModule(
        IJsonRpcConfig rpcConfig,
        IBlockchainBridge blockchainBridge,
        IBlockFinder blockFinder,
        IReceiptFinder receiptFinder,
        IStateReader stateReader,
        ITxPool txPool,
        ITxSender txSender,
        IWallet wallet,
        ILogManager logManager,
        ISpecProvider specProvider,
        IGasPriceOracle gasPriceOracle,
        IEthSyncingInfo ethSyncingInfo,
        IFeeHistoryOracle feeHistoryOracle,
        ulong? secondsPerSlot,

        IJsonRpcClient? sequencerRpcClient,
        IEthereumEcdsa ecdsa,
        ITxSealer sealer,
        IOptimismSpecHelper opSpecHelper) : base(
       rpcConfig,
       blockchainBridge,
       blockFinder,
       receiptFinder,
       stateReader,
       txPool,
       txSender,
       wallet,
       logManager,
       specProvider,
       gasPriceOracle,
       ethSyncingInfo,
       feeHistoryOracle,
       secondsPerSlot)
    {
        _sequencerRpcClient = sequencerRpcClient;
        _ecdsa = ecdsa;
        _sealer = sealer;
        _opSpecHelper = opSpecHelper;
    }

    public new ResultWrapper<OptimismReceiptForRpc[]?> eth_getBlockReceipts(BlockParameter blockParameter)
    {
        static ResultWrapper<OptimismReceiptForRpc[]?> GetBlockReceipts(IReceiptFinder receiptFinder, BlockParameter blockParameter, IBlockFinder blockFinder, ISpecProvider specProvider, IOptimismSpecHelper opSpecHelper)
        {
            SearchResult<Block> searchResult = blockFinder.SearchForBlock(blockParameter);
            if (searchResult.IsError)
            {
                return ResultWrapper<OptimismReceiptForRpc[]?>.Success(null);
            }

            Block? block = searchResult.Object!;
            OptimismTxReceipt[] receipts = receiptFinder.Get(block).Cast<OptimismTxReceipt>().ToArray() ?? new OptimismTxReceipt[block.Transactions.Length];
            IReleaseSpec spec = specProvider.GetSpec(block.Header);

            L1BlockGasInfo l1BlockGasInfo = new(block, opSpecHelper);

            OptimismReceiptForRpc[]? result = [.. receipts
                .Zip(block.Transactions, (r, t) =>
                {
                    return new OptimismReceiptForRpc(t.Hash!, r, t.GetGasInfo(spec, block.Header), l1BlockGasInfo.GetTxGasInfo(t), receipts.GetBlockLogFirstIndex(r.Index));
                })];
            return ResultWrapper<OptimismReceiptForRpc[]?>.Success(result);
        }

        return GetBlockReceipts(_receiptFinder, blockParameter, _blockFinder, _specProvider, _opSpecHelper);
    }

    public override async Task<ResultWrapper<Hash256>> eth_sendTransaction(TransactionForRpc rpcTx)
    {
        Transaction tx = rpcTx.ToTransaction();
        tx.ChainId = _blockchainBridge.GetChainId();
        tx.SenderAddress ??= _ecdsa.RecoverAddress(tx);

        if (tx.SenderAddress is null)
        {
            return ResultWrapper<Hash256>.Fail("Failed to recover sender");
        }

        await _sealer.Seal(tx, TxHandlingOptions.None);

        return await eth_sendRawTransaction(Rlp.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes);
    }

    public override async Task<ResultWrapper<Hash256>> eth_sendRawTransaction(byte[] transaction)
    {
        if (_sequencerRpcClient is null)
        {
            return await base.eth_sendRawTransaction(transaction);
        }

        Hash256? result = await _sequencerRpcClient.Post<Hash256>(nameof(eth_sendRawTransaction), transaction);
        if (result is null)
        {
            return ResultWrapper<Hash256>.Fail("Failed to forward transaction");
        }

        return ResultWrapper<Hash256>.Success(result);
    }

    public new ResultWrapper<OptimismReceiptForRpc?> eth_getTransactionReceipt(Hash256 txHash)
    {
        (TxReceipt? receipt, TxGasInfo? gasInfo, int logIndexStart) = _blockchainBridge.GetReceiptAndGasInfo(txHash);
        if (receipt is null || gasInfo is null)
        {
            return ResultWrapper<OptimismReceiptForRpc?>.Success(null);
        }

        SearchResult<Block> foundBlock = _blockFinder.SearchForBlock(new(receipt.BlockHash!));
        if (foundBlock.Object is null)
        {
            return ResultWrapper<OptimismReceiptForRpc?>.Success(null);
        }

        Block block = foundBlock.Object;

        L1BlockGasInfo l1GasInfo = new(block, _opSpecHelper);
        return ResultWrapper<OptimismReceiptForRpc?>.Success(
            new(txHash, (OptimismTxReceipt)receipt, gasInfo.Value, l1GasInfo.GetTxGasInfo(block.Transactions.First(tx => tx.Hash == txHash)), logIndexStart));
    }

    public override ResultWrapper<TransactionForRpc?> eth_getTransactionByHash(Hash256 transactionHash)
    {
        (TxReceipt? receipt, Transaction? transaction, UInt256? baseFee) = _blockchainBridge.GetTransaction(transactionHash, checkTxnPool: true);
        if (transaction is null)
        {
            return ResultWrapper<TransactionForRpc?>.Success(null);
        }

        RecoverTxSenderIfNeeded(transaction);
        TransactionForRpc transactionModel = TransactionForRpc.FromTransaction(transaction: transaction, blockHash: receipt?.BlockHash, blockNumber: receipt?.BlockNumber, txIndex: receipt?.Index, baseFee: baseFee, chainId: _specProvider.ChainId);
        if (transactionModel is DepositTransactionForRpc depositTx)
        {
            depositTx.DepositReceiptVersion = (receipt as OptimismTxReceipt)?.DepositReceiptVersion;
        }
        if (_logger.IsTrace) _logger.Trace($"eth_getTransactionByHash request {transactionHash}, result: {transactionModel.Hash}");
        return ResultWrapper<TransactionForRpc?>.Success(transactionModel);
    }

    protected override ResultWrapper<TransactionForRpc> GetTransactionByBlockAndIndex(BlockParameter blockParameter, UInt256 positionIndex)
    {
        SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter);
        if (searchResult.IsError || searchResult.Object is null)
        {
            return GetFailureResult<TransactionForRpc, Block>(searchResult, _ethSyncingInfo.SyncMode.HaveNotSyncedBodiesYet());
        }

        Block block = searchResult.Object;
        if (positionIndex < 0 || positionIndex > block!.Transactions.Length - 1)
        {
            return ResultWrapper<TransactionForRpc>.Fail("Position Index is incorrect", ErrorCodes.InvalidParams);
        }

        Transaction transaction = block.Transactions[(int)positionIndex];
        RecoverTxSenderIfNeeded(transaction);

        var receipt = _receiptFinder
            .Get(block)
            .FirstOrDefault(r => r.TxHash == transaction.Hash);

        TransactionForRpc transactionModel = TransactionForRpc.FromTransaction(transaction: transaction, blockHash: receipt?.BlockHash, blockNumber: receipt?.BlockNumber, txIndex: receipt?.Index, baseFee: block.BaseFeePerGas, chainId: _specProvider.ChainId);
        if (transactionModel is DepositTransactionForRpc depositTx)
        {
            depositTx.DepositReceiptVersion = (receipt as OptimismTxReceipt)?.DepositReceiptVersion;
        }

        return ResultWrapper<TransactionForRpc>.Success(transactionModel);
    }

    protected override ResultWrapper<BlockForRpc?> GetBlock(BlockParameter blockParameter, bool returnFullTransactionObjects)
    {
        SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter, true);
        if (searchResult.IsError)
        {
            return GetFailureResult<BlockForRpc?, Block>(searchResult, _ethSyncingInfo.SyncMode.HaveNotSyncedBodiesYet());
        }

        Block? block = searchResult.Object;

        if (block is null)
        {
            return ResultWrapper<BlockForRpc?>.Success(null);
        }

        BlockForRpc result = new BlockForRpc(block, false, _specProvider);

        if (returnFullTransactionObjects)
        {
            _blockchainBridge.RecoverTxSenders(block);
            OptimismTxReceipt[] receipts = _receiptFinder.Get(block).Cast<OptimismTxReceipt>().ToArray();
            result.Transactions = result.Transactions.Select((hash, index) =>
            {
                var transactionModel = TransactionForRpc.FromTransaction(
                    transaction: block.Transactions[index],
                    blockHash: block.Hash,
                    blockNumber: block.Number,
                    txIndex: index);
                if (transactionModel is DepositTransactionForRpc depositTx)
                {
                    OptimismTxReceipt? receipt = receipts.FirstOrDefault(r => r.TxHash?.Equals(hash) ?? false);
                    depositTx.DepositReceiptVersion = receipt?.DepositReceiptVersion;
                }

                return transactionModel;
            });
        }

        return ResultWrapper<BlockForRpc?>.Success(result);
    }
}
