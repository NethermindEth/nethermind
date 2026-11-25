// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Optimism.CL;
using Nethermind.Optimism.CL.Decoding;
using Nethermind.Optimism.CL.L1Bridge;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.CL;

public class DriverTests
{
    [TestCase(1UL, false)]
    [TestCase(11155111UL, false)]
    [TestCase(3151908UL, true)]
    public void Requires_known_fork_schedule_of_l1_chain(ulong l1ChainId, bool throws)
    {
        CLChainSpecEngineParameters parameters = new()
        {
            L2BlockTime = 2,
            SystemConfigProxy = TestItem.AddressA,
            L1ChainId = l1ChainId,
        };

        Assert.That(() => new Driver(
            Substitute.For<IL1Bridge>(),
            Substitute.For<IDecodingPipeline>(),
            parameters,
            Substitute.For<IExecutionEngineManager>(),
            Substitute.For<IL2Api>(),
            10,
            0,
            LimboLogs.Instance).Dispose(),
            throws ? Throws.ArgumentException : Throws.Nothing);
    }
}
