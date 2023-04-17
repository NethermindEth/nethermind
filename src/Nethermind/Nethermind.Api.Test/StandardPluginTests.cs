// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config.Test;

namespace Nethermind.Api.Test
{
    public static class StandardPluginTests
    {
        public static void Run()
        {
            Monitoring.Test.MetricsTests.ValidateMetricsDescriptions();
            StandardConfigTests.ValidateDefaultValues();
            StandardConfigTests.ValidateDescriptions();
        }
    }
}
