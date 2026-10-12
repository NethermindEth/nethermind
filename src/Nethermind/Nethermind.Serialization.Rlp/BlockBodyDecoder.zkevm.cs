// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp;

// Computed on access, so the guest runs no class constructor for this type.
public sealed partial class BlockBodyDecoder
{
    public static RlpLimit UnclesCountLimit => new(2, nameof(BlockBody), nameof(BlockBody.Uncles).AsMemory());

    public static RlpLimit WithdrawalsCountLimit => new(64_000, nameof(BlockBody), nameof(BlockBody.Withdrawals).AsMemory());
}
