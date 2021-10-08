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

namespace Nethermind.Core2.P2p
{
    public class PeeringStatus
    {
        public PeeringStatus(ForkVersion headForkVersion, Root finalizedRoot, Epoch finalizedEpoch, Root headRoot,
            Slot headSlot)
        {
            HeadForkVersion = headForkVersion;
            FinalizedRoot = finalizedRoot;
            FinalizedEpoch = finalizedEpoch;
            HeadRoot = headRoot;
            HeadSlot = headSlot;
        }

        public Epoch FinalizedEpoch { get; }
        public Root FinalizedRoot { get; }
        public ForkVersion HeadForkVersion { get; }
        public Root HeadRoot { get; }
        public Slot HeadSlot { get; }

        public override string ToString()
        {
            return
                $"fe={FinalizedEpoch}_fr={FinalizedRoot.ToString().Substring(0, 10)}_hs={HeadSlot}_hr={HeadRoot.ToString().Substring(0, 10)}";
        }
    }
}