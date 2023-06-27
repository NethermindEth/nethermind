// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Abi;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Consensus.AuRa.Validators
{
    public partial class ContractBasedValidator : ITxSource
    {
        private readonly long _posdaoTransition;

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes)
        {
            if (ForSealing)
            {
                var newBlockNumber = parent.Number + 1;
                if (newBlockNumber < _posdaoTransition)
                {
                    if (_logger.IsTrace) _logger.Trace("Skipping a call to emitInitiateChange");
                }
                else
                {
                    bool emitInitChangeCallable = false;

                    try
                    {
                        emitInitChangeCallable = ValidatorContract.EmitInitiateChangeCallable(parent);
                    }
                    catch (AbiException e)
                    {
                        if (_logger.IsError) _logger.Error($"Call to {nameof(ValidatorContract.EmitInitiateChangeCallable)} failed.", e);
                    }

                    if (emitInitChangeCallable)
                    {
                        if (_logger.IsTrace) _logger.Trace($"New block #{newBlockNumber} issued ― calling emitInitiateChange()");
                        Metrics.EmitInitiateChange++;
                        yield return ValidatorContract.EmitInitiateChange();
                    }
                    else
                    {
                        if (_logger.IsTrace) _logger.Trace($"New block #{newBlockNumber} issued ― no need to call emitInitiateChange()");
                    }
                }
            }
        }
    }
}
