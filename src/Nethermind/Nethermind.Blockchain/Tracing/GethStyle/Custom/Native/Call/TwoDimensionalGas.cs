// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;

// EIP-8037 two-dimensional gas fields written together on the top-level callTracer frame.
public readonly record struct TwoDimensionalGas(ulong RegularGasUsed, ulong StateGasUsed, ulong GasRefund);
