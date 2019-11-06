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
    public class AttestationData
    {
        public Slot Slot { get; set; }
        public CommitteeIndex CommitteeIndex { get; set; }
        public Sha256 BeaconBlockRoot { get; set; }
        public Checkpoint Source { get; set; }
        public Checkpoint Target { get; set; }

        public bool IsSlashable(AttestationData data2)
        {
            return (!ReferenceEquals(this, data2) && Target.Epoch == data2.Target.Epoch)
                   || (Source.Epoch < data2.Source.Epoch && Target.Epoch > data2.Target.Epoch);
        }
    }
}