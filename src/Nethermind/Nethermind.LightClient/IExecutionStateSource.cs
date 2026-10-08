// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
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
    Task<Hash256[]> GetAncestorHashesAsync(VerifiedHead head, ulong firstNumber, CancellationToken cancellationToken);
    Task<BlockHeader> GetCanonicalHeaderAsync(VerifiedHead head, ulong number, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<Block> GetBlockAsync(BlockHeader header, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<TxReceipt[]> GetReceiptsAsync(Block block, CancellationToken cancellationToken) => throw new NotSupportedException();
}
