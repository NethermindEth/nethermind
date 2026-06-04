// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc;

public interface ISigningTxCache
{
    void CacheSigningTransactions(Block block);
    Transaction[] GetSigningTransactions(Hash256 blockHash, long blockNumber, IXdcReleaseSpec spec);
    bool TryGetHeader(Hash256 blockHash, out XdcBlockHeader? header);
    void SetSigningTransactions(Hash256 blockHash, Transaction[] transactions);
}
