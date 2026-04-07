// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc.Contracts;

public interface IMintedRecordContract
{
    void UpdateAccounting(
        ITransactionProcessor transactionProcessor,
        XdcBlockHeader header,
        IXdcReleaseSpec spec,
        UInt256 mintedInEpoch,
        UInt256 burnedInEpoch);
}
