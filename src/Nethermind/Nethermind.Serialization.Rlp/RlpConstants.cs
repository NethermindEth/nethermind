// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Rlp;

public static class RlpConstants
{
    // across all supported chains
    public const int MaxTargetBlockGasLimit = 420_000_000;
    public const int MaxGenesisBlockGasLimit = 480_000_000;

    public static readonly int MaxGasLimit = Math.Max(MaxTargetBlockGasLimit, MaxGenesisBlockGasLimit);
}
