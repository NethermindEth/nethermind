//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System.Collections.Generic;
using Nethermind.Abi;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Consensus.AuRa.Validators
{
    public partial class ContractBasedValidator : ITxSource
    {
        private readonly long _posdaoTransition;
        
        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit)
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
