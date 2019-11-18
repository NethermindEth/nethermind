using System;
using System.Collections.Generic;
using System.Text;

namespace Cortex.BeaconNode.Tests.EpochProcessing
{
    public enum TestProcessStep
    {
        None = 0,
        ProcessJustificationAndFinalization,
        //ProcessCrosslinks,
        ProcessRewardsAndPenalties,
    }
}
