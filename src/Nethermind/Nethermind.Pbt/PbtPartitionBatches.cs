// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>Owns the independently prepared small account/code and wide storage batches.</summary>
public sealed class PbtPartitionBatches : IDisposable
{
    internal PbtWriteBatch<PbtPath>? Account { get; set; }
    internal PbtWriteBatch<PbtPath>? Code { get; set; }
    internal PbtWriteBatch<PbtStoragePath>? Storage { get; set; }

    /// <inheritdoc/>
    public void Dispose()
    {
        Account?.Dispose();
        Code?.Dispose();
        Storage?.Dispose();
    }
}
