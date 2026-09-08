// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>Owns the independently prepared small account/code and wide storage batches.</summary>
internal sealed class PbtPartitionBatches : IDisposable
{
    internal PbtWriteBatch<PbtFullKey>? Account { get; set; }
    internal PbtWriteBatch<PbtFullKey>? Code { get; set; }
    internal PbtWriteBatch<PbtStorageFullKey>? Storage { get; set; }

    public void Dispose()
    {
        Account?.Dispose();
        Code?.Dispose();
        Storage?.Dispose();
    }
}
