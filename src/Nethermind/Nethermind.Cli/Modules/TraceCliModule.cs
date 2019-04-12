/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Evm.Tracing;

namespace Nethermind.Cli.Modules
{
    [CliModule]
    public class ParityCliModule : CliModuleBase
    {
        [CliFunction("trace", "replayTransaction")]
        public ParityLikeTxTrace ReplayTransaction(string txHash, string[] traceTypes)
        {
            ParityLikeTxTrace parityLikeTxTrace = NodeManager.Post<ParityLikeTxTrace>("trace_replayTransaction", txHash, traceTypes).Result;
            return parityLikeTxTrace;
        }
        
        [CliFunction("trace", "replayBlockTransactions")]
        public ParityLikeTxTrace ReplayBlockTransactions(string blockNumber, string[] traceTypes)
        {
            ParityLikeTxTrace parityLikeTxTrace = NodeManager.Post<ParityLikeTxTrace>("trace_replayBlockTransactions", blockNumber, traceTypes).Result;
            return parityLikeTxTrace;
        }

        public ParityCliModule(ICliEngine cliEngine, INodeManager nodeManager) : base(cliEngine, nodeManager)
        {
        }
    }
}