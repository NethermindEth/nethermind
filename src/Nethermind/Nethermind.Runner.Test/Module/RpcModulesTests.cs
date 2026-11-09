// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Init.Modules;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class RpcModulesTests
{
    [TestCase(null, 32, ExpectedResult = 32)]
    [TestCase(null, 64, ExpectedResult = 64)]
    [TestCase(2, 64, ExpectedResult = 2)]
    public int Trace_module_capacity_defaults_to_processor_count(int? configured, int processorCount) =>
        RpcModules.GetTraceModuleCapacity(configured, processorCount);
}
