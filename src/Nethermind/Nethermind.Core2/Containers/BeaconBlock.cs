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

using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    public class BeaconBlock
    {
        public Slot Slot { get; set; }
        public Sha256 ParentRoot { get; set; }
        public Sha256 StateRoot { get; set; }
        public BeaconBlockBody Body { get; set; }
        public BlsSignature Signature { get; set; }

        public static uint MaxProposerSlashings { get; set; } = 16;

        public static uint MaxAttesterSlashings { get; set; } = 1;

        public static uint MaxAttestations { get; set; } = 128;

        public static uint MaxDeposits { get; set; } = 16;
        
        public static uint MaxVoluntaryExits { get; set; } = 16;
    }
}