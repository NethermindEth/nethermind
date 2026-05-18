// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;

namespace Ethereum.Test.Base;

public interface ITestBlockTracer : IBlockTracer
{
    void TestFinished(string testName, bool pass, IReleaseSpec spec, TimeSpan? duration, Hash256? headStateRoot);
}
