using System.Threading.Tasks;
using NUnit.Framework;

namespace Nethermind.IntegrationTests;

// Pre-warm both Docker images so parallel workers don't serialize behind the first build.
[SetUpFixture]
public class GlobalSetup
{
    [OneTimeSetUp]
    public Task BuildImagesOnce() => Task.WhenAll(
        Utils.GetNethermindImageAsync(),
        Utils.GetEngineApiProxyImageAsync());
}
