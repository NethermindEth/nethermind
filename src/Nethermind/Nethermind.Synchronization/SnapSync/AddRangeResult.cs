using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Synchronization.SnapSync
{
    internal enum AddRangeResult
    {
        OK,
        MissingRootHashInProofs,
        DifferentRootHash
    }
}
