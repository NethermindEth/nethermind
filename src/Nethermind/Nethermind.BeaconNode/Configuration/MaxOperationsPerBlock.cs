namespace Nethermind.BeaconNode.Configuration
{
    public class MaxOperationsPerBlock
    {
        public ulong MaximumAttestations { get; set; }
        public ulong MaximumAttesterSlashings { get; set; }
        public ulong MaximumDeposits { get; set; }
        public ulong MaximumProposerSlashings { get; set; }
        public ulong MaximumVoluntaryExits { get; set; }
    }
}
