using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Containers
{
    public class Fork
    {
        public Fork(ForkVersion previousVersion, ForkVersion currentVersion, Epoch epoch)
        {
            PreviousVersion = previousVersion;
            CurrentVersion = currentVersion;
            Epoch = epoch;
        }

        public ForkVersion CurrentVersion { get; }

        /// <summary>Gets the epoch of the latest fork</summary>
        public Epoch Epoch { get; }

        public ForkVersion PreviousVersion { get; }

        public static Fork Clone(Fork other)
        {
            var clone = new Fork(
                other.PreviousVersion,
                other.PreviousVersion,
                other.Epoch);
            return clone;
        }

        public override string ToString()
        {
            return $"E:{Epoch} C:{CurrentVersion} P:{PreviousVersion}";
        }
    }
}
