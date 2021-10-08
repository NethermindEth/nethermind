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
using System.Linq;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Test.Helpers
{
    public static class TestCheckpoint
    {
        public static IList<Checkpoint> GetCheckpoints(Epoch epoch)
        {
            var checkpoints = new List<Checkpoint>();
            if (epoch >= new Epoch(1))
            {
                checkpoints.Add(new Checkpoint(new Epoch((ulong)epoch - 1), new Root(Enumerable.Repeat((byte)0xaa, 32).ToArray())));
            }
            if (epoch >= new Epoch(2))
            {
                checkpoints.Add(new Checkpoint(new Epoch((ulong)epoch - 2), new Root(Enumerable.Repeat((byte)0xbb, 32).ToArray())));
            }
            if (epoch >= new Epoch(3))
            {
                checkpoints.Add(new Checkpoint(new Epoch((ulong)epoch - 3), new Root(Enumerable.Repeat((byte)0xcc, 32).ToArray())));
            }
            if (epoch >= new Epoch(4))
            {
                checkpoints.Add(new Checkpoint(new Epoch((ulong)epoch - 4), new Root(Enumerable.Repeat((byte)0xdd, 32).ToArray())));
            }
            if (epoch >= new Epoch(5))
            {
                checkpoints.Add(new Checkpoint(new Epoch((ulong)epoch - 5), new Root(Enumerable.Repeat((byte)0xee, 32).ToArray())));
            }
            return checkpoints;
        }
    }
}
