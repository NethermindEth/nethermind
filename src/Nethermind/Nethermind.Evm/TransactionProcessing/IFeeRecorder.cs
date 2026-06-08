// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.TransactionProcessing;

/// <summary>
/// Diverts in-tx fee credits away from <see cref="State.IWorldState"/> writes so they can be
/// accumulated outside the per-tx journal.
/// </summary>
/// <remarks>
/// Block-STM uses this seam to break the otherwise-universal write-after-write dependency that
/// every tx's coinbase/fee-collector credit would otherwise create. When non-null,
/// <see cref="TransactionProcessor"/> calls <see cref="RecordFee"/> instead of writing directly
/// to the recipient's balance; the executor reconstructs the balances at PushChanges time.
/// </remarks>
public interface IFeeRecorder
{
    /// <param name="recipient">Fee recipient address (gas beneficiary or EIP-1559 fee collector).</param>
    /// <param name="amount">Fee amount in wei.</param>
    /// <param name="createAccount">When true the recipient must be created if it does not yet exist.</param>
    void RecordFee(Address recipient, in UInt256 amount, bool createAccount);
}
