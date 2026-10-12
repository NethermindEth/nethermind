// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp.TxDecoders;

public abstract partial class BaseTxDecoder
{
    // 30MB should be good enough for 300MGas block just filled with call data
    private static readonly RlpLimit _dataRlpLimit = RlpLimit.For<Transaction>((int)30.MiB, nameof(Transaction.Data));
}
