using System.Threading.Tasks;
using Nethermind.Overseer.Test.Framework;
using NUnit.Framework;

namespace Nethermind.Overseer.Test
{
    public class CliqueTests : TestBuilder
    {
        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public async Task One_validator()
        {
            StartCliqueMiner("val1")
                .Wait(5000)
                .Kill("val1");

            await ScenarioCompletion;
        }

        [Test]
        public async Task Two_validators()
        {
            StartCliqueMiner("val1")
                .StartCliqueMiner("val2")
                .Wait(10000)
                .Kill("val1")
                .Kill("val2");

            await ScenarioCompletion;
        }
    }
}