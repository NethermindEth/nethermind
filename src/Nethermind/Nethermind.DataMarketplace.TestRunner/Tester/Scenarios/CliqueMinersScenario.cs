using System.Collections.Generic;
using Nethermind.DataMarketplace.TestRunner.Tester.Steps;

namespace Nethermind.DataMarketplace.TestRunner.Tester.Scenarios
{
    public class CliqueMinersScenario : ITestScenario
    {
        public string Name => "Launch Node";
        
        public IEnumerable<TestStepBase> Steps { get; }

        public CliqueMinersScenario(TestBuilder testBuilder)
        {
            Steps = testBuilder
                .NewScenario()
                .StartCliqueNode("validator1")
                .Wait(3000)
                .StartCliqueNode("validator2")
                .Wait(40000)
                .Kill("validator1")
                .Kill("validator2")
                .Build();
        }
    }
}