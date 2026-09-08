// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules.IndexProof;

/// <summary>
/// RPC module for EIP-8304 trustless log and transaction index proofs.
/// </summary>
/// <remarks>
/// Provides SSZ Merkle proofs that a given index entry is included in a table root
/// stored in the system contract. Combined with <c>eth_getProof</c> on the contract's
/// storage slot, this enables full verification against the world state root.
/// The <c>level</c> parameter selects the table hierarchy level (0–4); the block number
/// identifies any block the table covers and is aligned down to the table's first block.
/// <para>See <see href="https://eips.ethereum.org/EIPS/eip-8304">EIP-8304</see>.</para>
/// </remarks>
[RpcModule(ModuleType.IndexProof)]
public interface IIndexProofRpcModule : IRpcModule
{
    [JsonRpcMethod(
        Description = "Returns an SSZ Merkle proof that a transaction is included in the EIP-8304 index table of the given level covering the given block.",
        IsImplemented = true,
        IsSharable = true)]
    ResultWrapper<IndexProofResult?> indexProof_getTransactionProof(Hash256 txHash, long blockNumber, int level = 0);

    [JsonRpcMethod(
        Description = "Returns SSZ Merkle proofs for all log entries matching the given address in the EIP-8304 index table of the given level covering the given block.",
        IsImplemented = true,
        IsSharable = true)]
    ResultWrapper<IndexProofResult[]?> indexProof_getLogAddressProofs(Address address, long blockNumber, int level = 0);

    [JsonRpcMethod(
        Description = "Returns the storage slot and table parameters for the table of the given level covering the given block, enabling eth_getProof-based verification.",
        IsImplemented = true,
        IsSharable = true)]
    ResultWrapper<StorageSlotInfo> indexProof_getStorageSlot(int level, long firstBlock);
}
