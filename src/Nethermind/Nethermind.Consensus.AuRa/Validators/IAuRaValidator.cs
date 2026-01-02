// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Validators
{
    public interface IAuRaValidator
    {
        Address[] Validators { get; }
        void OnBlockProcessingStart(Block block, ProcessingOptions options = ProcessingOptions.None);
        void OnBlockProcessingEnd(Block block, TxReceipt[] receipts, ProcessingOptions options = ProcessingOptions.None);
    }
}
