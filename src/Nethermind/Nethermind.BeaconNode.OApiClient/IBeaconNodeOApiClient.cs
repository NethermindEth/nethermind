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

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.BeaconNode.OApiClient
{
    public interface IBeaconNodeOApiClient
    {
        string BaseUrl { get; }
        Task<string> VersionAsync(CancellationToken cancellationToken);
//        Task<Response> SyncingAsync(CancellationToken cancellationToken);
//        Task<Response2> ForkAsync(CancellationToken cancellationToken);
//        Task<Validator> ValidatorAsync(byte[] pubkey, CancellationToken cancellationToken);
        Task<ICollection<ValidatorDuty>> DutiesAsync(IEnumerable<byte[]> validator_pubkeys, ulong? epoch,
            CancellationToken cancellationToken);
        Task<BeaconBlock> BlockAsync(ulong slot, byte[] randao_reveal, CancellationToken cancellationToken);
        Task<ulong> TimeAsync(CancellationToken cancellationToken);
    }
}