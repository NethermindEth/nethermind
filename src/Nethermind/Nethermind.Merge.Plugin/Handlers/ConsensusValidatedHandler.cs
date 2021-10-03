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

using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers
{
    /// <summary>
    /// https://hackmd.io/@n0ble/consensus_api_design_space
    /// Communicates that full consensus validation of an execution payload is complete along with its corresponding status
    /// </summary>
    public class ConsensusValidatedHandler : IHandler<ConsensusValidatedRequest, string>
    {
        private readonly PayloadManager _payloadManager;

        public ConsensusValidatedHandler(PayloadManager payloadManager)
        {
            _payloadManager = payloadManager;
        }

        public ResultWrapper<string> Handle(ConsensusValidatedRequest request)
        {
            if (!_payloadManager.CheckIfExecutePayloadIsFinished(request.BlockHash, out var executePayloadIsFinished))
            {
                return ResultWrapper<string>.Fail($"Unknown blockHash: {request.BlockHash}", MergeErrorCodes.UnknownHeader);
            }
            
            bool isValid = (request.Status & ConsensusValidationStatus.Valid) != 0;

            if (executePayloadIsFinished && isValid)
            {
                _payloadManager.ProcessValidatedPayload(request.BlockHash);
            }
            else
            {
                _payloadManager.TryAddConsensusValidatedResult(request.BlockHash, isValid);
            }
            
            return ResultWrapper<string>.Success(null);
        }
    }
}
