using System;
using Nevermind.Core.Crypto;

namespace Nevermind.Network
{
    public class NodeDistanceMetric
    {
        /// <summary>
        /// xor distance metric is based on sha3(nodeid)
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public int GetDistance(PublicKey a, PublicKey b)
        {
//            Keccak hashA = Keccak.Compute(a);
//            Keccak hashB = Keccak.Compute(b);
            throw new NotImplementedException();
        }
    }
}