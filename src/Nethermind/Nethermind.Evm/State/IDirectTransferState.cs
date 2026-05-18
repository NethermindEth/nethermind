// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm.State;

/// <summary>
/// Provides direct block-level state access for the plain ether transfer fast path,
/// bypassing the intra-transaction journal and Commit overhead.
/// </summary>
public interface IDirectTransferState
{
    /// <summary>
    /// Read account directly from block-level cache, bypassing the intra-tx journal.
    /// Returns null if the account does not exist.
    /// </summary>
    Account? GetAccountDirect(Address address);

    /// <summary>
    /// Apply a plain ether transfer directly to block-level state, bypassing the
    /// intra-tx journal and Commit. Handles overlapping addresses (self-transfer,
    /// sender==beneficiary, etc.) via sequential read-after-write.
    /// EIP-158 empty account cleanup is handled for recipient and beneficiary.
    /// </summary>
    void ApplyPlainTransferDirect(
        Address sender, UInt256 newSenderNonce,
        in UInt256 senderGasReservation, in UInt256 senderRefund,
        Address recipient, in UInt256 transferValue,
        Address beneficiary, in UInt256 beneficiaryFee,
        Address? feeCollector, in UInt256 collectedFees,
        IReleaseSpec spec);
}
