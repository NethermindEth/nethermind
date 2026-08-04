// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;

/// <summary>
/// EIP-8037 two-dimensional gas breakdown attached to the top-level <c>callTracer</c> frame for
/// Amsterdam+ blocks. Kept as a single value so the three fields are always written together.
/// </summary>
public readonly record struct TwoDimensionalGas(ulong RegularGasUsed, ulong StateGasUsed, ulong GasRefund);
