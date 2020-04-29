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

namespace Nethermind.BeaconNode.OApi.Models
{
    public class BeaconBlockBody
    {
        // TODO: Support attestations, slashings, etc
        
        //public IList<AttesterSlashing> AttesterSlashings { get; set; }
        //public IList<Attestation> Attestations { get; set; }
        public IList<Deposit>? Deposits { get; set; }
        public Eth1Data? Eth1Data { get; set; }
        public byte[]? Graffiti { get; set; }
        //public IList<ProposerSlashing> ProposerSlashings { get; set; }
        public byte[]? RandaoReveal { get; set; }
        //public IList<SignedVoluntaryExit> VoluntaryExits { get; set; }

    }
}
