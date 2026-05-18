using System.Threading.Tasks;
using NUnit.Framework;

namespace Nethermind.IntegrationTests;

// Without this, the first test to call BuildNethermindContainerAsync triggers the
// Dockerfile build while every other parallel worker blocks on s_imageBuildLock.
// That serializes test startup behind the build and makes higher worker counts
// look like they aren't speeding anything up.
[SetUpFixture]
public class GlobalSetup
{
    [OneTimeSetUp]
    public Task BuildImageOnce() => Utils.GetNethermindImageAsync();
}
