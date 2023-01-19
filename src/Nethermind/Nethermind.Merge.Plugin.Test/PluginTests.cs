// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config.Test;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class PluginTests
    {
        [Test]
        public void All_json_rpc_methods_are_documented()
        {
            JsonRpc.Test.StandardJsonRpcTests.ValidateDocumentation();
        }


        [Test]
        public void All_metrics_are_described()
        {
            Monitoring.Test.MetricsTests.ValidateMetricsDescriptions();
        }

        [Test]
        public void All_default_values_are_correct()
        {
            StandardConfigTests.ValidateDefaultValues();
        }

        [Test]
        public void All_config_items_have_descriptions_or_are_hidden()
        {
            StandardConfigTests.ValidateDescriptions();
        }
    }
}
