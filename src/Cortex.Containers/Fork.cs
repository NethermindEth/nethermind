namespace Cortex.Containers
{
    public class Fork
    {
        public ForkVersion CurrentVersion { get; }

        /// <summary>Gets the epoch of the latest fork</summary>
        public Epoch Epoch { get; }

        public ForkVersion PreviousVersion { get; }

        public override string ToString()
        {
            return $"E:{Epoch} C:{CurrentVersion} P:{PreviousVersion}";
        }
    }
}
