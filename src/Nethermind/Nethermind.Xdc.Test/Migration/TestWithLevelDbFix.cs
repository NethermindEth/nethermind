// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Xdc.Migration;

namespace Nethermind.Xdc.Test.Migration;

public abstract class TestWithLevelDbFix
{
    static TestWithLevelDbFix()
    {
        RuntimeHelpers.RunClassConstructor(typeof(LevelReadOnlyDbAdapter).TypeHandle);
    }
}
