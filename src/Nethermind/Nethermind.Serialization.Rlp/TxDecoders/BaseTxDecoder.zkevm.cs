// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp.TxDecoders;

// Computed on access, so the guest runs no class constructor for this type.
public abstract partial class BaseTxDecoder
{
    private static RlpLimit _dataRlpLimit => new((int)30.MiB, nameof(Transaction), nameof(Transaction.Data).AsMemory());
}
