using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Containers
{
    public class ProposerSlashing
    {
        public ProposerSlashing(
            ValidatorIndex proposerIndex,
            BeaconBlockHeader header1,
            BeaconBlockHeader header2)
        {
            ProposerIndex = proposerIndex;
            Header1 = header1;
            Header2 = header2;
        }

        public BeaconBlockHeader Header1 { get; }
        public BeaconBlockHeader Header2 { get; }
        public ValidatorIndex ProposerIndex { get; }

        public override string ToString()
        {
            return $"P:{ProposerIndex} for B1:({Header1})";
        }
    }
}
