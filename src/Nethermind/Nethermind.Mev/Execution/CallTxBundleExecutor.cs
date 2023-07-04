// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Text;
using Nethermind.Consensus;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Mev.Data;

namespace Nethermind.Mev.Execution
{
    public class CallTxBundleExecutor : TxBundleExecutor<TxsResults, BlockCallOutputTracer>
    {

        public CallTxBundleExecutor(ITracerFactory tracer, ISpecProvider specProvider, ISigner? signer) : base(tracer, specProvider, signer)
        {
        }

        protected override TxsResults BuildResult(MevBundle bundle, BlockCallOutputTracer tracer)
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
