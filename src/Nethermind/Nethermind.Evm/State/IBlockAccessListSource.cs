// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Evm.State;

/// <summary>
/// Surfaces the per-tx BAL slice currently being recorded so transaction processing
/// can read in-flight changes (e.g. EIP-8037 self-destruct refund accounting).
/// </summary>
public interface IBlockAccessListSource
{
    BlockAccessListAtIndex? GeneratedBlockAccessList { get; }
}
