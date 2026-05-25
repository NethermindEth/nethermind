using System.Threading.Tasks;
using NUnit.Framework;

namespace Nethermind.IntegrationTests;

// Without this, the first test to call BuildNethermindContainerAsync triggers the
// Dockerfile build while every other parallel worker blocks on s_imageBuildLock.
// That serializes test startup behind the build and makes higher worker counts
// look like they aren't speeding anything up. The proxy image is built in parallel
// for the same reason — EngineApiProxyTests would otherwise pay the build cost
// serially after the Nethermind image finishes.
[SetUpFixture]
public class GlobalSetup
{
    [OneTimeSetUp]
    public Task BuildImagesOnce() => Task.WhenAll(
        Utils.GetNethermindImageAsync(),
        Utils.GetEngineApiProxyImageAsync());
}
