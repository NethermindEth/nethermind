// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Serialization.Rlp;

public partial record struct RlpLimit
{
    // We shouldn't allocate any single array bigger than 4M
    public static readonly RlpLimit DefaultLimit = new();
    public static readonly RlpLimit Bloom = For<Bloom>(Core.Bloom.ByteLength);
    public static readonly RlpLimit L4 = new(4);
    public static readonly RlpLimit L8 = new(8);
    public static readonly RlpLimit L32 = new(32);
    public static readonly RlpLimit L64 = new(64);
    public static readonly RlpLimit L65 = new(65);

    /// <summary>Maximum number of logs a single receipt may carry.</summary>
    /// <remarks>
    /// A log costs at least <see cref="GasCostOf.Log"/> and a receipt's logs all come from one
    /// transaction, whose gas cannot exceed the block's, so <see cref="MaxBlockGas"/> bounds the count.
    /// Clamped because the configured ceiling is unbounded; one below <see cref="int.MaxValue"/> so
    /// that the <c>Limit + 1</c> peek idiom cannot wrap. Recomputed by <see cref="InitMaxBlockGas"/>
    /// rather than derived per read - receipt decoding is a hot path.
    /// </remarks>
    public static RlpLimit ReceiptLogs { get; private set; } = ForReceiptLogs(DefaultMaxBlockGas);
}
