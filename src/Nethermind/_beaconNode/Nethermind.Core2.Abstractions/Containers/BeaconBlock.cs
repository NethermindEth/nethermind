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
        public BeaconBlock(Slot slot, Root parentRoot, Root stateRoot, BeaconBlockBody body)
        {
            Slot = slot;
            ParentRoot = parentRoot;
            StateRoot = stateRoot;
            Body = body;
        }

        public BeaconBlockBody Body { get; }
        public Root ParentRoot { get; }
        public Slot Slot { get; private set; }
        public Root StateRoot { get; private set; }

        public void SetStateRoot(Root stateRoot) => StateRoot = stateRoot;

        public override string ToString()
        {
            return $"s={Slot}_p={ParentRoot.ToString().Substring(0, 10)}_st={StateRoot.ToString().Substring(0, 10)}";
        }
    }
}
