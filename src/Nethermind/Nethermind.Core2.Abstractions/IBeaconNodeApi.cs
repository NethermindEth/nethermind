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

//using System.Collections.Generic;
//using System.Threading;
//using System.Threading.Tasks;
//using Nethermind.Core2.Containers;
//using Nethermind.Core2.Crypto;
//using Nethermind.Core2.Types;
//using BeaconBlock = Nethermind.BeaconNode.Containers.BeaconBlock;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core2.Api;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2
{
    /// <summary>
    /// API specification for the beacon node, which enables a validator to connect and perform its obligations on the Ethereum 2.0 phase 0 beacon chain.
    /// </summary>
    /// <remarks>
    /// This interface contains async methods, as for a remote client all of them will involve a network call.
    /// </remarks>
    public interface IBeaconNodeApi
    {
        Task<ApiResponse<ulong>> GetGenesisTimeAsync(CancellationToken cancellationToken);
        Task<ApiResponse<Fork>> GetNodeForkAsync(CancellationToken cancellationToken);
        Task<ApiResponse<string>> GetNodeVersionAsync(CancellationToken cancellationToken);
        Task<ApiResponse<Syncing>> GetSyncingAsync(CancellationToken cancellationToken);

        Task<ApiResponse<Attestation>> NewAttestationAsync(BlsPublicKey validatorPublicKey, bool proofOfCustodyBit,
            Slot slot, CommitteeIndex index, CancellationToken cancellationToken);

        Task<ApiResponse<BeaconBlock>> NewBlockAsync(Slot slot, BlsSignature randaoReveal,
            CancellationToken cancellationToken);

        Task<ApiResponse> PublishAttestationAsync(Attestation signedAttestation, CancellationToken cancellationToken);
        Task<ApiResponse> PublishBlockAsync(SignedBeaconBlock signedBlock, CancellationToken cancellationToken);

        Task<ApiResponse<IList<ValidatorDuty>>> ValidatorDutiesAsync(IList<BlsPublicKey> validatorPublicKeys,
            Epoch? epoch, CancellationToken cancellationToken);
    }
}