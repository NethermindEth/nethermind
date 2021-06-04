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

using System.Linq;
using System.Text;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Mev.Data;

namespace Nethermind.Mev.Execution
{
    public class CallTxBundleExecutor : TxBundleExecutor<TxsResults, BlockCallOutputTracer>
    {
        
        public CallTxBundleExecutor(ITracerFactory tracer, Address? beneficiaryAddress) : base(tracer, beneficiaryAddress)
        {
        }

        protected override TxsResults BuildResult(MevBundle bundle, Block block, BlockCallOutputTracer tracer, Keccak resultStateRoot)
        {
            TxResult ToTxResult(CallOutputTracer callOutputTracer)
            {
                TxResult result = new();
                if (callOutputTracer.StatusCode == StatusCode.Success)
                {
                    result.Value = callOutputTracer.ReturnValue;
                }
                else
                {
                    result.Error = callOutputTracer.ReturnValue;
                }
                return result;
            }

            return new TxsResults(tracer.BuildResults().ToDictionary(
                kvp => kvp.Key,
                kvp => ToTxResult(kvp.Value)));
        }

        protected override BlockCallOutputTracer CreateBlockTracer(MevBundle mevBundle) => new();
    }
}
