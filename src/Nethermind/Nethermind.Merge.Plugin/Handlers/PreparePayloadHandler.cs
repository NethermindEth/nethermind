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

using System;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.Handlers
{
    /// <summary>
    /// https://hackmd.io/@n0ble/consensus_api_design_space
    /// engine_preparePayload. Notifies an execution client that the consensus client will need to propose a block
    /// at some point in the future and that the payload will be requested by the corresponding engine_getPayload
    /// near to that point in time. This call also supplies an execution client with inputs required to produce
    /// a payload e.g. random.
    /// One of the purposes of this call is giving an execution client some time to get prepared to the subsequent
    /// engine_getPayload call that is required to be responded immediately with the most up-to-date version of
    /// the payload that is available by the time of the get call.
    /// One of the potential implementations of this method would be initiating a payload building process
    /// that builds an execution payload on top of a given parent with transactions selected from the mempool
    /// and arguments provided in the parameter set of the call. And then would keep this payload updated with
    /// the most recent state of the mempool.
    /// As the first action, it is recommended for implementations to build a payload with empty transaction
    /// set as a backup in order to be able to respond immediately if the corresponding engine_getPayload
    /// call happens e.g. 10ms after the prepare one which could be the case.
    /// Execution client should cancel the process of payload building
    /// (if there is a constant process of updating the payload) if SECONDS_PER_SLOT seconds have passed since
    /// the timestamp specified in the call. This suggestion is made to protect execution client from wasting
    /// resources in the edge case when related engine_getPayload call never happens.
    /// If the corresponding engine_getPayload call happens after the cancellation it should be responded with error.
    /// Related engine_getPayload call will likely happen very close to the timestamp.
    /// Execution client may use this information to choose the strategy of building a payload.
    /// A pair of engine_preparePayload and engine_getPayload related to each other are identified by the payload_id
    /// parameter. Consensus client implementations are free to use whatever value of the identifier they find reasonable.
    /// </summary>
    public class PreparePayloadHandler: IHandler<PreparePayloadRequest, PreparePayloadResult>
    {
        private readonly IBlockTree _blockTree;
        private readonly PayloadStorage _payloadStorage;
        private readonly IManualBlockProductionTrigger _blockProductionTrigger;
        private readonly IManualBlockProductionTrigger _emptyBlockProductionTrigger;
        private readonly ManualTimestamper _timestamper;
        private readonly ISealer _sealer;
        private readonly IStateProvider _stateProvider;
        private readonly ILogger _logger;
        private readonly IBlockchainProcessor _processor;
        private readonly IInitConfig _initConfig;

        public PreparePayloadHandler(
            IBlockTree blockTree, 
            PayloadStorage payloadStorage,
            // TODO: hide this complexity -> prepare payload should not really implement the logic of delivering empty vs meaningful block
            IManualBlockProductionTrigger blockProductionTrigger, 
            IManualBlockProductionTrigger emptyBlockProductionTrigger, 
            ManualTimestamper timestamper, 
            ISealer sealer,
            IStateProvider stateProvider,
            IBlockchainProcessor processor,
            IInitConfig initConfig,
            ILogManager logManager)
        {
            _blockTree = blockTree;
            _payloadStorage = payloadStorage;
            _blockProductionTrigger = blockProductionTrigger;
            _emptyBlockProductionTrigger = emptyBlockProductionTrigger;
            _timestamper = timestamper;
            _sealer = sealer;
            _stateProvider = stateProvider;
            _processor = processor;
            _initConfig = initConfig;
            _logger = logManager.GetClassLogger();
        }

        public ResultWrapper<PreparePayloadResult> Handle(PreparePayloadRequest request)
        {
            // add syncing check when implementation will be ready
            // if (_ethSyncingInfo.IsSyncing())
            // {
            //     executePayloadResult.Status = VerificationStatus.Syncing;
            //     return ResultWrapper<PreparePayloadResult>.Fail($"Syncing in progress.",MergeErrorCodes.ActionNotAllowed);
            // }
            
            BlockHeader? parentHeader = _blockTree.FindHeader(request.ParentHash);
            if (parentHeader is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Parent block {request.ParentHash} cannot be found. New block will not be produced.");
                return ResultWrapper<PreparePayloadResult>.Fail(
                    $"Parent block {request.ParentHash} cannot be found. New block will not be produced.",
                    MergeErrorCodes.UnknownHeader);
            }

            _timestamper.Set(DateTimeOffset.FromUnixTimeSeconds((long) request.Timestamp).UtcDateTime);

            uint payloadId = _payloadStorage.RentNextPayloadId();
            
            
            Address blockAuthor = request.FeeRecipient == Address.Zero ? _sealer.Address : request.FeeRecipient;
            Task generatePayloadTask =
                _payloadStorage.GeneratePayload(payloadId, request.Random, parentHeader, blockAuthor, request.Timestamp); // not awaiting on purpose
            
            return ResultWrapper<PreparePayloadResult>.Success(new PreparePayloadResult(payloadId));
        }
    }
}
