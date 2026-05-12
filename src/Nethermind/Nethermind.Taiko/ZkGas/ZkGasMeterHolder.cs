// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Taiko.ZkGas;

/// <summary>
/// Scoped shared state that exposes the current block's <see cref="ZkGasMeter"/>
/// to both <see cref="TaikoBlockProcessor"/> and the block-transactions executor.
/// <see cref="TaikoBlockProcessor"/> sets <see cref="Meter"/> at the start of each
/// block (via <see cref="ZkGasBlockTracer"/>), and the executor reads it between
/// transactions to stop inclusion when the ZK gas block limit is exceeded.
/// </summary>
public class ZkGasMeterHolder
{
    /// <summary>
    /// The ZK gas meter for the block currently being processed, or <c>null</c>
    /// when no block is in progress.
    /// </summary>
    public ZkGasMeter? Meter { get; set; }
}
