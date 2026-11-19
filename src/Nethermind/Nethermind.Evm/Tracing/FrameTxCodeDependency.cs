// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.Tracing;

/// <summary>An account an EIP-8141 validation prefix relied on, with the code hash it held then.</summary>
/// <param name="Account">The account whose code could change (EIP-8298), or whose code decides what EIP-8151 ecRecover returns for it.</param>
/// <param name="CodeHash">The code hash the prefix ran against.</param>
/// <param name="InPaymasterFrame">Whether the prefix relied on it only in the pay frame of a paymaster other than tx.sender.</param>
public readonly record struct FrameTxCodeDependency(Address Account, ValueHash256 CodeHash, bool InPaymasterFrame = false);
