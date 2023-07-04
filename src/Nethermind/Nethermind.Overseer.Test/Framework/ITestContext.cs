// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Overseer.Test.Framework
{
    // Marker
    public interface ITestContext
    {
        void SetBuilder(TestBuilder builder);
    }

    public interface ITestState
    {
    }
}
