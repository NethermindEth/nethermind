using System.Numerics;

namespace Nethermind.Core.Specs
{
    public class MainNetSpecProvider : ISpecProvider
    {
        public IReleaseSpec GetSpec(BigInteger blockNumber)
        {
            if (blockNumber < 1150000)
            {
                return Frontier.Instance;
            }

            if (blockNumber < 1920000)
            {
                return Homestead.Instance;
            }

            if (blockNumber < 2463000)
            {
                return Dao.Instance;
            }

            // DAO 1920000 - needs review and implementation
            if (blockNumber < 2675000)
            {
                return TangerineWhistle.Instance;
            }

            if (blockNumber < 4370000)
            {
                return SpuriousDragon.Instance;
            }


            return Byzantium.Instance;
        }
    }
}