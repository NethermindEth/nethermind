using System.Collections.Generic;
using System.Dynamic;
using Nethermind.DataMarketplace.TestRunner.Tester.Steps;
using Nethermind.Stats;

namespace Nethermind.DataMarketplace.TestRunner.Tester.Scenarios
{
    public class LaunchNodeScenario : ITestScenario
    {
        public string Name => "Launch Node";
        
        public IEnumerable<TestStepBase> Steps { get; }

        public LaunchNodeScenario(TestBuilder testBuilder)
        {
            Steps = testBuilder
                .NewScenario()
                .StartCliqueNode("node1")
                .Wait(3000)
                .Kill()
                .Build();
        }
    }
}