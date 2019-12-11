using System.Collections.Generic;
using System.Linq;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Tests.Helpers
{
    public static class TestCheckpoint
    {
        public static IList<Checkpoint> GetCheckpoints(Epoch epoch)
        {
            var checkpoints = new List<Checkpoint>();
            if (epoch >= new Epoch(1))
            {
                checkpoints.Add(new Checkpoint(new Epoch((ulong)epoch - 1), new Hash32(Enumerable.Repeat((byte)0xaa, 32).ToArray())));
            }
            if (epoch >= new Epoch(2))
            {
                checkpoints.Add(new Checkpoint(new Epoch((ulong)epoch - 2), new Hash32(Enumerable.Repeat((byte)0xbb, 32).ToArray())));
            }
            if (epoch >= new Epoch(3))
            {
                checkpoints.Add(new Checkpoint(new Epoch((ulong)epoch - 3), new Hash32(Enumerable.Repeat((byte)0xcc, 32).ToArray())));
            }
            if (epoch >= new Epoch(4))
            {
                checkpoints.Add(new Checkpoint(new Epoch((ulong)epoch - 4), new Hash32(Enumerable.Repeat((byte)0xdd, 32).ToArray())));
            }
            if (epoch >= new Epoch(5))
            {
                checkpoints.Add(new Checkpoint(new Epoch((ulong)epoch - 5), new Hash32(Enumerable.Repeat((byte)0xee, 32).ToArray())));
            }
            return checkpoints;
        }
    }
}
