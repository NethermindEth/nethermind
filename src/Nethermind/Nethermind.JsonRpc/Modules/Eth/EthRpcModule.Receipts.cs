// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.JsonRpc.Modules.Eth;

public class EthRpcModule : EthRpcModule<ReceiptForRpc>, IEthRpcModule
{
    public EthRpcModule(
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
       IFeeHistoryOracle feeHistoryOracle) : base(

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
    }

    public override ResultWrapper<ReceiptForRpc> eth_getTransactionReceipt(Hash256 txHash)
    {
        (TxReceipt? receipt, TxGasInfo? gasInfo, int logIndexStart) = _blockchainBridge.GetReceiptAndGasInfo(txHash);
        if (receipt is null || gasInfo is null)
        {
            return ResultWrapper<ReceiptForRpc>.Success(null);
        }

        if (_logger.IsTrace) _logger.Trace($"eth_getTransactionReceipt request {txHash}, result: {txHash}");
        return ResultWrapper<ReceiptForRpc>.Success(new(txHash, receipt, gasInfo.Value, logIndexStart));
    }


    public override ResultWrapper<ReceiptForRpc[]> eth_getBlockReceipts(BlockParameter blockParameter)
    {
        return _receiptFinder.GetBlockReceipts(blockParameter, _blockFinder, _specProvider);
    }
}
