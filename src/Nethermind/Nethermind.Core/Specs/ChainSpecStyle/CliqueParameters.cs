using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Specs.ChainSpecStyle
{
    public class CliqueParameters
    {
        public ulong Epoch { get; set; }

        public ulong Period { get; set; }

        public UInt256? Reward { get; set; }
    }
}