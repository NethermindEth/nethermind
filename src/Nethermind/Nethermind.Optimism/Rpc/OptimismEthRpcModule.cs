// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using Nethermind.TxPool;

namespace Nethermind.Optimism;

public class OptimismEthRpcModule : IEthRpcModule
{
    private readonly IEthRpcModule _ethRpcModule;
    private readonly IJsonRpcClient? _sequencerRpcClient;
    private readonly IBlockchainBridge _blockchainBridge;
    private readonly IAccountStateProvider _accountStateProvider;
    private readonly IEthereumEcdsa _ecdsa;
    private readonly ITxSealer _sealer;

    public OptimismEthRpcModule(IEthRpcModule ethRpcModule, IJsonRpcClient? sequencerRpcClient,
        IBlockchainBridge blockchainBridge, IAccountStateProvider accountStateProvider, IEthereumEcdsa ecdsa, ITxSealer sealer)
    {
        _ethRpcModule = ethRpcModule;
        _sequencerRpcClient = sequencerRpcClient;
        _blockchainBridge = blockchainBridge;
        _accountStateProvider = accountStateProvider;
        _ecdsa = ecdsa;
        _sealer = sealer;
    }

    public ResultWrapper<ulong> eth_chainId()
    {
        return _ethRpcModule.eth_chainId();
    }

    public ResultWrapper<string> eth_protocolVersion()
    {
        return _ethRpcModule.eth_protocolVersion();
    }

    public ResultWrapper<SyncingResult> eth_syncing()
    {
        return _ethRpcModule.eth_syncing();
    }

    public ResultWrapper<Address> eth_coinbase()
    {
        return _ethRpcModule.eth_coinbase();
    }

    public ResultWrapper<FeeHistoryResults> eth_feeHistory(int blockCount, BlockParameter newestBlock, double[]? rewardPercentiles = null)
    {
        return _ethRpcModule.eth_feeHistory(blockCount, newestBlock, rewardPercentiles);
    }

    public ResultWrapper<byte[]> eth_snapshot()
    {
        return _ethRpcModule.eth_snapshot();
    }

    public ResultWrapper<UInt256?> eth_maxPriorityFeePerGas()
    {
        return _ethRpcModule.eth_maxPriorityFeePerGas();
    }

    public ResultWrapper<UInt256?> eth_gasPrice()
    {
        return _ethRpcModule.eth_gasPrice();
    }

    public ResultWrapper<UInt256?> eth_blobBaseFee()
    {
        return _ethRpcModule.eth_blobBaseFee();
    }

    public ResultWrapper<IEnumerable<Address>> eth_accounts()
    {
        return _ethRpcModule.eth_accounts();
    }

    public Task<ResultWrapper<long?>> eth_blockNumber()
    {
        return _ethRpcModule.eth_blockNumber();
    }

    public Task<ResultWrapper<UInt256?>> eth_getBalance(Address address, BlockParameter? blockParameter = null)
    {
        return _ethRpcModule.eth_getBalance(address, blockParameter);
    }

    public ResultWrapper<byte[]> eth_getStorageAt(Address address, UInt256 positionIndex, BlockParameter? blockParameter = null)
    {
        return _ethRpcModule.eth_getStorageAt(address, positionIndex, blockParameter);
    }

    public Task<ResultWrapper<UInt256>> eth_getTransactionCount(Address address, BlockParameter? blockParameter = null)
    {
        return _ethRpcModule.eth_getTransactionCount(address, blockParameter);
    }

    public ResultWrapper<UInt256?> eth_getBlockTransactionCountByHash(Hash256 blockHash)
    {
        return _ethRpcModule.eth_getBlockTransactionCountByHash(blockHash);
    }

    public ResultWrapper<UInt256?> eth_getBlockTransactionCountByNumber(BlockParameter blockParameter)
    {
        return _ethRpcModule.eth_getBlockTransactionCountByNumber(blockParameter);
    }

    public ResultWrapper<ReceiptForRpc[]> eth_getBlockReceipts(BlockParameter blockParameter)
    {
        return _ethRpcModule.eth_getBlockReceipts(blockParameter);
    }

    public ResultWrapper<UInt256?> eth_getUncleCountByBlockHash(Hash256 blockHash)
    {
        return _ethRpcModule.eth_getUncleCountByBlockHash(blockHash);
    }

    public ResultWrapper<UInt256?> eth_getUncleCountByBlockNumber(BlockParameter blockParameter)
    {
        return _ethRpcModule.eth_getUncleCountByBlockNumber(blockParameter);
    }

    public ResultWrapper<byte[]> eth_getCode(Address address, BlockParameter? blockParameter = null)
    {
        return _ethRpcModule.eth_getCode(address, blockParameter);
    }

    public ResultWrapper<byte[]> eth_sign(Address addressData, byte[] message)
    {
        return _ethRpcModule.eth_sign(addressData, message);
    }

    public async Task<ResultWrapper<Hash256>> eth_sendTransaction(TransactionForRpc rpcTx)
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

    public async Task<ResultWrapper<Hash256>> eth_sendRawTransaction(byte[] transaction)
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

    public ResultWrapper<string> eth_call(TransactionForRpc transactionCall, BlockParameter? blockParameter = null)
    {
        return _ethRpcModule.eth_call(transactionCall, blockParameter);
    }

    public ResultWrapper<IReadOnlyList<SimulateBlockResult>> eth_simulateV1(SimulatePayload<TransactionForRpc> payload, BlockParameter? blockParameter = null)
    {
        return _ethRpcModule.eth_simulateV1(payload, blockParameter);
    }

    public ResultWrapper<UInt256?> eth_estimateGas(TransactionForRpc transactionCall, BlockParameter? blockParameter = null)
    {
        return _ethRpcModule.eth_estimateGas(transactionCall, blockParameter);
    }

    public ResultWrapper<AccessListForRpc?> eth_createAccessList(TransactionForRpc transactionCall, BlockParameter? blockParameter = null,
        bool optimize = true)
    {
        return _ethRpcModule.eth_createAccessList(transactionCall, blockParameter, optimize);
    }

    public ResultWrapper<BlockForRpc> eth_getBlockByHash(Hash256 blockHash, bool returnFullTransactionObjects = false)
    {
        return _ethRpcModule.eth_getBlockByHash(blockHash, returnFullTransactionObjects);
    }

    public ResultWrapper<BlockForRpc> eth_getBlockByNumber(BlockParameter blockParameter, bool returnFullTransactionObjects = false)
    {
        return _ethRpcModule.eth_getBlockByNumber(blockParameter, returnFullTransactionObjects);
    }

    public Task<ResultWrapper<TransactionForRpc>> eth_getTransactionByHash(Hash256 transactionHash)
    {
        return _ethRpcModule.eth_getTransactionByHash(transactionHash);
    }

    public ResultWrapper<TransactionForRpc[]> eth_pendingTransactions()
    {
        return _ethRpcModule.eth_pendingTransactions();
    }

    public ResultWrapper<TransactionForRpc> eth_getTransactionByBlockHashAndIndex(Hash256 blockHash, UInt256 positionIndex)
    {
        return _ethRpcModule.eth_getTransactionByBlockHashAndIndex(blockHash, positionIndex);
    }

    public ResultWrapper<TransactionForRpc> eth_getTransactionByBlockNumberAndIndex(BlockParameter blockParameter, UInt256 positionIndex)
    {
        return _ethRpcModule.eth_getTransactionByBlockNumberAndIndex(blockParameter, positionIndex);
    }

    public Task<ResultWrapper<ReceiptForRpc>> eth_getTransactionReceipt(Hash256 txHashData)
    {
        return _ethRpcModule.eth_getTransactionReceipt(txHashData);
    }

    public ResultWrapper<BlockForRpc> eth_getUncleByBlockHashAndIndex(Hash256 blockHashData, UInt256 positionIndex)
    {
        return _ethRpcModule.eth_getUncleByBlockHashAndIndex(blockHashData, positionIndex);
    }

    public ResultWrapper<BlockForRpc> eth_getUncleByBlockNumberAndIndex(BlockParameter blockParameter, UInt256 positionIndex)
    {
        return _ethRpcModule.eth_getUncleByBlockNumberAndIndex(blockParameter, positionIndex);
    }

    public ResultWrapper<UInt256?> eth_newFilter(Filter filter)
    {
        return _ethRpcModule.eth_newFilter(filter);
    }

    public ResultWrapper<UInt256?> eth_newBlockFilter()
    {
        return _ethRpcModule.eth_newBlockFilter();
    }

    public ResultWrapper<UInt256?> eth_newPendingTransactionFilter()
    {
        return _ethRpcModule.eth_newPendingTransactionFilter();
    }

    public ResultWrapper<bool?> eth_uninstallFilter(UInt256 filterId)
    {
        return _ethRpcModule.eth_uninstallFilter(filterId);
    }

    public ResultWrapper<IEnumerable<object>> eth_getFilterChanges(UInt256 filterId)
    {
        return _ethRpcModule.eth_getFilterChanges(filterId);
    }

    public ResultWrapper<IEnumerable<FilterLog>> eth_getFilterLogs(UInt256 filterId)
    {
        return _ethRpcModule.eth_getFilterLogs(filterId);
    }

    public ResultWrapper<IEnumerable<FilterLog>> eth_getLogs(Filter filter)
    {
        return _ethRpcModule.eth_getLogs(filter);
    }

    public ResultWrapper<AccountProof> eth_getProof(Address accountAddress, UInt256[] hashRate, BlockParameter blockParameter)
    {
        return _ethRpcModule.eth_getProof(accountAddress, hashRate, blockParameter);
    }

    public ResultWrapper<AccountForRpc?> eth_getAccount(Address accountAddress, BlockParameter? blockParameter = null)
    {
        return _ethRpcModule.eth_getAccount(accountAddress, blockParameter);
    }
}
