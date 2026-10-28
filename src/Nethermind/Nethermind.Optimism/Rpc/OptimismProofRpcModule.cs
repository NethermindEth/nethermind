// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.Serialization.Json;

namespace Nethermind.Optimism.Rpc;

/// <summary>
/// Completes deposit transactions served by <c>proof_getTransactionByHash</c> with the fields only their receipt carries.
/// </summary>
/// <remarks>
/// The inner module proves a transaction without reading its receipt. A deposit transaction's nonce lives in its
/// receipt, so one whose receipt is missing returns <c>null</c>, as <c>eth_getTransactionByHash</c> does, rather
/// than being served with a nonce of zero.
/// </remarks>
public class OptimismProofRpcModule(IProofRpcModule inner, IReceiptFinder receiptFinder) : IProofRpcModule
{
    public ResultWrapper<CallResultWithProof> proof_call(TransactionForRpc tx, BlockParameter blockParameter) =>
        inner.proof_call(tx, blockParameter);

    public ResultWrapper<TransactionForRpcWithProof?> proof_getTransactionByHash(Hash256 txHash, bool includeHeader)
    {
        ResultWrapper<TransactionForRpcWithProof?> result = inner.proof_getTransactionByHash(txHash, includeHeader);
        if (result.Data?.Transaction is not DepositTransactionForRpc deposit || deposit.BlockHash is not { } blockHash)
        {
            return result;
        }

        if (receiptFinder.Get(blockHash).ForTransaction(txHash) is not OptimismTxReceipt receipt)
        {
            return ResultWrapper<TransactionForRpcWithProof?>.Success(null);
        }

        deposit.Nonce = receipt.DepositNonce ?? 0;
        deposit.DepositReceiptVersion = receipt.DepositReceiptVersion;
        return result;
    }

    public ResultWrapper<ReceiptWithProof?> proof_getTransactionReceipt(Hash256 txHash, bool includeHeader) =>
        inner.proof_getTransactionReceipt(txHash, includeHeader);

    public ResultWrapper<AccountProofWithMeta> proof_getProofWithMeta(Address accountAddress, StorageKeys storageKeys, BlockParameter? blockParameter) =>
        inner.proof_getProofWithMeta(accountAddress, storageKeys, blockParameter);
}
