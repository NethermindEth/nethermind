// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Api;
using Nethermind.Core.Memory;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Network.Config;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

public class ConfigureMemoryAllocatorsTests
{
    [Test]
    public void Sets_malloc_options_unless_disabled([Values] bool disableMallocOpts)
    {
        MallocHelper mallocHelper = Substitute.For<MallocHelper>();
        InitConfig initConfig = new() { DisableMallocOpts = disableMallocOpts };

        new ConfigureMemoryAllocators(initConfig, new NetworkConfig(), LimboLogs.Instance, mallocHelper).Execute(CancellationToken.None);

        mallocHelper.Received(disableMallocOpts ? 0 : 1).MallOpt(MallocHelper.Option.M_MMAP_THRESHOLD, Arg.Any<int>());
    }
}
