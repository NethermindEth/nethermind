namespace Cortex.Containers
{
    public class BeaconState
    {
        public BeaconState(ulong genesisTime)
        {
            GenesisTime = genesisTime;
        }

        public ulong GenesisTime { get; }
    }
}
