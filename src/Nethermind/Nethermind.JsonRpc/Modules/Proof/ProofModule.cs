//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Proof
{
    public class ProofModule : IProofModule
    {
        private readonly ILogger _logger;
        
        public ProofModule(ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        public ResultWrapper<CallResultWithProof> proof_call(TransactionForRpc tx, BlockParameter blockParameter)
        {
            throw new System.NotImplementedException();
        }

        public ResultWrapper<object> proof_getTransactionByHash(Keccak txHash, bool includeHeader = true)
        {
            throw new System.NotImplementedException();
        }

        public ResultWrapper<object> proof_getTransactionReceipt(Keccak txHash, bool includeHeader = true)
        {
            throw new System.NotImplementedException();
        }
    }
}