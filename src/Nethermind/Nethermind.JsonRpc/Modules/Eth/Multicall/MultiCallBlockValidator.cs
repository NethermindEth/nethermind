// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.Eth.Multicall;

internal class MultiCallBlockValidator : BlockValidator
{
    public MultiCallBlockValidator(ITxValidator? txValidator,
        IHeaderValidator? headerValidator,
        IUnclesValidator? unclesValidator,
        ISpecProvider? specProvider,
        ILogManager? logManager) : base(txValidator, headerValidator, unclesValidator, specProvider, logManager)
    {
    }

    public override bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock)
    {
        if (processedBlock.Header.StateRoot != suggestedBlock.Header.StateRoot)
        {
            //Note we mutate suggested block here to allow eth_multicall enforced State Root change
            //and still pass validation 
            suggestedBlock.Header.StateRoot = processedBlock.Header.StateRoot;
            suggestedBlock.Header.Hash = suggestedBlock.Header.CalculateHash();
        }

        return base.ValidateProcessedBlock(processedBlock, receipts, suggestedBlock);
    }
}
