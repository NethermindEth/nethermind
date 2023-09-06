// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Memory;
using NUnit.Framework;

namespace Nethermind.Core.Test.Memory;

public class MemoryPressureHelperTests
{
    [Test]
    public void SmokeTest()
    {
        MemoryPressureHelper.Instance.GetCurrentMemoryPressure();
    }
}
