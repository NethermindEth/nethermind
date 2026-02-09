// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.TransactionProcessing;

/// <summary>
/// Records fee transfers that are applied outside the immediate transaction state.
/// </summary>
public interface IFeeRecorder
{
    /// <summary>
    /// Records a fee payment for the current transaction.
    /// </summary>
    /// <param name="recipient">Fee recipient address.</param>
    /// <param name="amount">Fee amount.</param>
    /// <param name="createAccount">True when the fee transfer should create the recipient account if missing.</param>
    void RecordFee(Address recipient, in UInt256 amount, bool createAccount);
}
