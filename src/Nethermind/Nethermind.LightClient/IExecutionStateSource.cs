// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.LightClient;

internal interface IExecutionStateSource
{
    Task<Account> GetAccountAsync(VerifiedHead head, Address address, CancellationToken cancellationToken);
    Task<UInt256> GetStorageAsync(VerifiedHead head, Address address, Account account, UInt256 key, CancellationToken cancellationToken);
    Task<byte[]> GetCodeAsync(Account account, CancellationToken cancellationToken);
    Task<BlockHeader> GetHeaderByHashAsync(Hash256 hash, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken cancellationToken);
    Task<ReadOnlyBlockAccessList> GetBlockAccessListAsync(BlockHeader header, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<Hash256[]> GetAncestorHashesAsync(VerifiedHead head, ulong firstNumber, CancellationToken cancellationToken);
    Task<BlockHeader> GetCanonicalHeaderAsync(VerifiedHead head, ulong number, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<BlockHeader[]> GetLogHeadersAsync(VerifiedHead anchor, VerifiedHead finalized, ulong firstNumber,
        ulong lastNumber, CancellationToken cancellationToken) =>
        VerifiedLogHeaders.GetAsync(this, anchor, finalized, firstNumber, lastNumber, cancellationToken);
    Task<Block> GetBlockAsync(BlockHeader header, CancellationToken cancellationToken) => throw new NotSupportedException();
    /// <summary>Authenticates the complete receipt list, using the exact transaction count when known.</summary>
    Task<TxReceipt[]> GetReceiptsAsync(BlockHeader header, CancellationToken cancellationToken, int? transactionCount = null) => throw new NotSupportedException();
    Task BroadcastTransactionAsync(Transaction transaction, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<ExecutionAccountProof> GetProofAsync(VerifiedHead head, Address address, UInt256[] storageKeys,
        CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed record ExecutionAccountProof(Account Account, byte[][] AccountProof, ExecutionStorageProof[] StorageProofs);
internal sealed record ExecutionStorageProof(UInt256 Key, UInt256 Value, byte[][] Proof);
