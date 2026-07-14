// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db.LogIndex;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Optimism.Rpc;

public class OptimismEthRpcModule(
    IJsonRpcConfig rpcConfig,
    IBlockchainBridge blockchainBridge,
    IBlockFinder blockFinder,
    IBlockTree blockTree,
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
    IProtocolsManager protocolsManager,
    IForkInfo forkInfo,
    ulong? secondsPerSlot,
    IJsonRpcClient? sequencerRpcClient,
    IEthereumEcdsa ecdsa,
    ITxSealer sealer,
    ILogIndexConfig? logIndexConfig,
    IReceiptConfig receiptConfig,
    IOptimismSpecHelper opSpecHelper,
    HeadBlockSignal headBlockSignal,
    IEthCapabilitiesProvider capabilitiesProvider,
    IBlockForRpcFactory blockForRpcFactory)
    : EthRpcModule(rpcConfig,
        blockchainBridge,
        blockFinder,
        blockTree,
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
        protocolsManager,
        forkInfo,
        logIndexConfig,
        receiptConfig,
        secondsPerSlot,
        headBlockSignal,
        capabilitiesProvider,
        blockForRpcFactory), IOptimismEthRpcModule
{
    public override ResultWrapper<ReceiptForRpc[]?> eth_getBlockReceipts(BlockParameter blockParameter)
    {
        SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter);
        if (searchResult.IsError)
        {
            return ResultWrapper<ReceiptForRpc[]?>.Success(null);
        }

        Block? block = searchResult.Object!;
        TxReceipt[] receipts = _receiptFinder.Get(block) ?? new TxReceipt[block.Transactions.Length];
        IReleaseSpec spec = _specProvider.GetSpec(block.Header);

        L1BlockGasInfo l1BlockGasInfo = new(block, opSpecHelper);

        OptimismReceiptForRpc[]? result = [.. receipts
                .Zip(block.Transactions, (receipt, tx) =>
                    receipt is OptimismTxReceipt optimismTxReceipt
                        ? new OptimismReceiptForRpc(
                            tx.Hash!,
                            optimismTxReceipt,
                            block.Timestamp,
                            tx.GetGasInfo(spec, block.Header),
                            l1BlockGasInfo.GetTxGasInfo(tx),
                            receipts.GetBlockLogFirstIndex(receipt.Index))
                        : new OptimismReceiptForRpc(
                            tx.Hash!,
                            receipt,
                            block.Timestamp,
                            tx.GetGasInfo(spec, block.Header),
                            receipts.GetBlockLogFirstIndex(receipt.Index)))];
        return ResultWrapper<ReceiptForRpc[]?>.Success(result);
    }

    public override async Task<ResultWrapper<Hash256>> eth_sendTransaction(SignableTransactionForRpc rpcTx)
    {
        Result<Transaction> txResult = rpcTx.ToTransaction(validateUserInput: true);
        if (!txResult.Success(out Transaction? tx, out string? error))
        {
            return ResultWrapper<Hash256>.Fail(error, ErrorCodes.InvalidInput);
        }

        tx.ChainId = _blockchainBridge.GetChainId();
        tx.SenderAddress ??= ecdsa.RecoverAddress(tx);

        if (tx.SenderAddress is null)
        {
            return ResultWrapper<Hash256>.Fail("Failed to recover sender");
        }

        if (!sealer.TrySeal(tx, TxHandlingOptions.None))
            return ResultWrapper<Hash256>.Fail("authentication needed: password or unlock", ErrorCodes.AccountLocked);

        return await eth_sendRawTransaction(Rlp.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes);
    }

    public override async Task<ResultWrapper<Hash256>> eth_sendRawTransaction(byte[] transaction)
    {
        if (sequencerRpcClient is null)
        {
            return await base.eth_sendRawTransaction(transaction);
        }

        Hash256? result = await sequencerRpcClient.Post<Hash256>(nameof(eth_sendRawTransaction), transaction);
        return result is null ? ResultWrapper<Hash256>.Fail("Failed to forward transaction") : ResultWrapper<Hash256>.Success(result);
    }

    public override ResultWrapper<ReceiptForRpc?> eth_getTransactionReceipt(Hash256 txHash)
    {
        (TxReceipt? receipt, ulong blockTimestamp, TxGasInfo? gasInfo, int logIndexStart) = _blockchainBridge.GetTxReceiptInfo(txHash);
        if (receipt is null || gasInfo is null)
        {
            return ResultWrapper<ReceiptForRpc?>.Success(null);
        }

        SearchResult<Block> foundBlock = _blockFinder.SearchForBlock(new(receipt.BlockHash!));
        if (foundBlock.Object is null)
        {
            return ResultWrapper<ReceiptForRpc?>.Success(null);
        }

        Block block = foundBlock.Object;
        L1BlockGasInfo l1GasInfo = new(block, opSpecHelper);
        OptimismReceiptForRpc result =
            receipt is OptimismTxReceipt optimismTxReceipt
                ? new OptimismReceiptForRpc(
                        txHash,
                        optimismTxReceipt,
                        blockTimestamp,
                        gasInfo.Value,
                        l1GasInfo.GetTxGasInfo(block.Transactions.First(tx => tx.Hash == txHash)),
                        logIndexStart)
                : new OptimismReceiptForRpc(
                        txHash,
                        receipt,
                        blockTimestamp,
                        gasInfo.Value,
                        logIndexStart);
        return ResultWrapper<ReceiptForRpc?>.Success(result);
    }

    public override ResultWrapper<TransactionForRpc?> eth_getTransactionByHash(Hash256 transactionHash)
    {
        if (!_blockchainBridge.TryGetTransaction(transactionHash, out TransactionLookupResult? transactionResult, checkTxnPool: true))
        {
            return ResultWrapper<TransactionForRpc?>.Success(null);
        }

        TransactionLookupResult result = transactionResult!.Value;
        Transaction transaction = result.Transaction!;

        RecoverTxSenderIfNeeded(transaction);
        TransactionForRpcContext extraData = result.ExtraData;
        TransactionForRpc transactionModel = TransactionForRpc.FromTransaction(
            transaction: transaction,
            extraData: extraData);
        if (transactionModel is DepositTransactionForRpc depositTx)
        {
            depositTx.DepositReceiptVersion = (extraData.Receipt as OptimismTxReceipt)?.DepositReceiptVersion;
        }
        if (_logger.IsTrace) _logger.Trace($"eth_getTransactionByHash request {transactionHash}, result: {transactionModel.Hash}");
        return ResultWrapper<TransactionForRpc?>.Success(transactionModel);
    }

    protected override ResultWrapper<TransactionForRpc?> GetTransactionByBlockAndIndex(BlockParameter blockParameter, UInt256 positionIndex)
    {
        SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter);
        if (searchResult.IsError || searchResult.Object is null)
        {
            return GetFailureResult<TransactionForRpc?, Block>(searchResult, _ethSyncingInfo.SyncMode.HaveNotSyncedBodiesYet());
        }

        Block block = searchResult.Object;
        if (positionIndex >= block!.Transactions.Length)
        {
            return ResultWrapper<TransactionForRpc?>.Success(null);
        }

        Transaction transaction = block.Transactions[(int)positionIndex];
        RecoverTxSenderIfNeeded(transaction);

        TxReceipt? receipt = TryGetMatchingReceipt(_receiptFinder.Get(block), transaction, (int)positionIndex);

        TransactionForRpc transactionModel = TransactionForRpc.FromTransaction(
            transaction,
            new(
                chainId: _specProvider.ChainId,
                blockHash: block.Hash!,
                blockNumber: block.Number,
                txIndex: (int)positionIndex,
                blockTimestamp: block.Timestamp,
                baseFee: block.BaseFeePerGas,
                receipt: receipt));
        if (transactionModel is DepositTransactionForRpc depositTx)
        {
            depositTx.DepositReceiptVersion = (receipt as OptimismTxReceipt)?.DepositReceiptVersion;
        }

        return ResultWrapper<TransactionForRpc?>.Success(transactionModel);
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

        BlockForRpc result = _blockForRpcFactory.Create(block, includeFullTransactionData: false, _specProvider, skipTxs: returnFullTransactionObjects);

        if (returnFullTransactionObjects)
        {
            if (block.Transactions.Length == 0)
            {
                result.Transactions = Array.Empty<TransactionForRpc>();
            }
            else
            {
                _blockchainBridge.RecoverTxSenders(block);
                TxReceipt[] receipts = _receiptFinder.Get(block);
                Transaction[] transactions = block.Transactions;
                TransactionForRpc[] txs = new TransactionForRpc[transactions.Length];
                for (int i = 0; i < txs.Length; i++)
                {
                    Transaction tx = transactions[i];
                    TxReceipt? receipt = TryGetMatchingReceipt(receipts, tx, i);
                    TransactionForRpc rpcTx = TransactionForRpc.FromTransaction(
                        tx,
                        new(
                            chainId: _specProvider.ChainId,
                            blockHash: block.Hash!,
                            blockNumber: block.Number,
                            txIndex: i,
                            blockTimestamp: block.Timestamp,
                            baseFee: block.BaseFeePerGas,
                            receipt: receipt));

                    if (rpcTx is DepositTransactionForRpc depositTx)
                    {
                        depositTx.DepositReceiptVersion = (receipt as OptimismTxReceipt)?.DepositReceiptVersion;
                    }

                    txs[i] = rpcTx;
                }

                result.Transactions = txs;
            }
        }

        return ResultWrapper<BlockForRpc?>.Success(result);
    }

    private static TxReceipt? TryGetMatchingReceipt(TxReceipt[] receipts, Transaction tx, int txIndex)
    {
        TxReceipt? indexedReceipt = txIndex < receipts.Length ? receipts[txIndex] : null;
        if (indexedReceipt?.TxHash == tx.Hash)
        {
            return indexedReceipt;
        }

        if (tx.Hash is not Hash256 txHash)
        {
            return null;
        }

        for (int i = 0; i < receipts.Length; i++)
        {
            TxReceipt receipt = receipts[i];
            if (receipt.TxHash == txHash)
            {
                return receipt;
            }
        }

        return null;
    }
}
