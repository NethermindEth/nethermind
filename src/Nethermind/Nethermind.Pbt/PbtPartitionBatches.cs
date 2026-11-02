// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>Owns the independently prepared small account/code and wide storage batches.</summary>
public sealed class PbtPartitionBatches : IDisposable
{
    public PbtWriteBatch<PbtPath>? Account { get; set; }
    public PbtWriteBatch<PbtPath>? Code { get; set; }
    public PbtWriteBatch<PbtStoragePath>? Storage { get; set; }

    /// <inheritdoc/>
    public void Dispose()
    {
        Account?.Dispose();
        Code?.Dispose();
        Storage?.Dispose();
    }
}
