using System;
using System.Collections.Generic;
using System.Text;

namespace Cortex.Cryptography
{
    public enum BlsScheme
    {
        Unknown = 0,
        Basic, 
        MessageAugmentation, 
        ProofOfPossession
    }
}
