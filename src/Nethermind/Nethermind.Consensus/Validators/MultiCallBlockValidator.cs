// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Validators;

public class MultiCallBlockValidator : BlockValidator
{
    public MultiCallBlockValidator(ITxValidator? txValidator, IHeaderValidator? headerValidator, IUnclesValidator? unclesValidator, ISpecProvider? specProvider, ILogManager? logManager) : base(txValidator, headerValidator, unclesValidator, specProvider, logManager)
    {
    }

    public override bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock)
    {
        return true;
    }
}
