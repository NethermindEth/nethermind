using System.Threading.Tasks;
using Nethermind.Overseer.Test.Framework;
using NUnit.Framework;

namespace Nethermind.Overseer.Test
{
    public class BasicTests : TestBuilder
    {
        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public async Task Test1()
        {
            StartCliqueNode("node1")
                .Wait(3000)
                .Kill();

            await ScenarioCompletion;
        }
    }
}