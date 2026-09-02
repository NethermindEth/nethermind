// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


namespace Nethermind.State.Flat.History.Proofs;

internal sealed class ResolutionBudget(long maxScannedRows)
{
    public const long DefaultMaxScannedRows = 250_000;

    private long _scannedRows;

    public long MaxScannedRows { get; } = maxScannedRows > 0 ? maxScannedRows : DefaultMaxScannedRows;

    public void ChargeRow()
    {
        if (Interlocked.Increment(ref _scannedRows) <= MaxScannedRows) return;

        throw new StateUnavailableException(
            $"Resolving this proof would have to read more than {MaxScannedRows} history rows, which means the " +
            "commitment column does not cover the requested height. Build the archive proof commitments for that " +
            "range, or raise FlatDb.ArchiveProofMaxScannedRows.");
    }
}
