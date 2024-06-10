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
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Optimism.Rpc;

public class OptimismEthRpcModule : EthRpcModule, IOptimismEthRpcModule
{
    private readonly IJsonRpcClient? _sequencerRpcClient;
    private readonly IAccountStateProvider _accountStateProvider;
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

        IJsonRpcClient? sequencerRpcClient,
        IAccountStateProvider accountStateProvider,
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
       feeHistoryOracle)
    {
        _sequencerRpcClient = sequencerRpcClient;
        _accountStateProvider = accountStateProvider;
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
            bool isEip1559Enabled = specProvider.GetSpec(block.Header).IsEip1559Enabled;

            L1BlockGasInfo l1BlockGasInfo = new(block, opSpecHelper);

            OptimismReceiptForRpc[]? result = [.. receipts
                .Zip(block.Transactions, (r, t) =>
                {
                    return new OptimismReceiptForRpc(t.Hash!, r, t.GetGasInfo(isEip1559Enabled, block.Header), l1BlockGasInfo.GetTxGasInfo(t), receipts.GetBlockLogFirstIndex(r.Index));
                })];
            return ResultWrapper<OptimismReceiptForRpc[]?>.Success(result);
        }

        return GetBlockReceipts(_receiptFinder, blockParameter, _blockFinder, _specProvider, _opSpecHelper);
    }

    public override async Task<ResultWrapper<Hash256>> eth_sendTransaction(TransactionForRpc rpcTx)
    {
        Transaction tx = rpcTx.ToTransactionWithDefaults(_blockchainBridge.GetChainId());
        tx.SenderAddress ??= _ecdsa.RecoverAddress(tx);

        if (tx.SenderAddress is null)
        {
            return ResultWrapper<Hash256>.Fail("Failed to recover sender");
        }

        if (rpcTx.Nonce is null)
        {
            tx.Nonce = _accountStateProvider.GetNonce(tx.SenderAddress);
        }

        await _sealer.Seal(tx, TxHandlingOptions.None);

        return await eth_sendRawTransaction(Rlp.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes);
    }

    public override async Task<ResultWrapper<Hash256>> eth_sendRawTransaction(byte[] transaction)
    {
        if (_sequencerRpcClient is null)
        {
            return ResultWrapper<Hash256>.Fail("No sequencer url in the config");
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
}
