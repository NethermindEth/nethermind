//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using Nethermind.Config.Test;
using NUnit.Framework;

namespace Nethermind.Clique.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class StandardTests
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
