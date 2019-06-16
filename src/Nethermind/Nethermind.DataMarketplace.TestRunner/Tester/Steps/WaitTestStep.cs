using System.Threading.Tasks;

namespace Nethermind.DataMarketplace.TestRunner.Tester.Steps
{
    public class WaitTestStep : TestStepBase
    {
        private readonly int _delay;

        public WaitTestStep(string name, int delay = 5000) : base(name)
        {
            _delay = delay;
        }

        public override async Task<TestResult> ExecuteAsync()
        {
            if (_delay > 0)
            {
                await Task.Delay(_delay);
            }

            return GetResult(true);
        }
    }
}