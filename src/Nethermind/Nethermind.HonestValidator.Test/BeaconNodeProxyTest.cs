using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode;
using Nethermind.BeaconNode.OApiClient;
using Nethermind.BeaconNode.Services;
using Nethermind.HonestValidator.Services;
using NSubstitute;
using NSubstitute.Core.Arguments;
using Shouldly;

namespace Nethermind.HonestValidator.Test
{
    [TestClass]
    public class BeaconNodeProxyTest
    {
        [TestMethod]
        public async Task BasicNodeVersion()
        {
            // Arrange
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection();

            IBeaconNodeOApiClient beaconNodeOApiClient = Substitute.For<IBeaconNodeOApiClient>();
            beaconNodeOApiClient.VersionAsync(Arg.Any<CancellationToken>()).Returns("TESTVERSION");
            IBeaconNodeOApiClientFactory beaconNodeOApiClientFactory = Substitute.For<IBeaconNodeOApiClientFactory>();
            beaconNodeOApiClientFactory.CreateClient(Arg.Any<string>()).Returns(beaconNodeOApiClient);
            testServiceCollection.AddSingleton<IBeaconNodeOApiClientFactory>(beaconNodeOApiClientFactory);
            
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
            
            // Act
            IBeaconNodeApi beaconNodeProxy = testServiceProvider.GetService<IBeaconNodeApi>();
            beaconNodeProxy.ShouldBeOfType(typeof(BeaconNodeProxy));
            string version = await beaconNodeProxy.GetNodeVersionAsync(CancellationToken.None);
            
            // Assert
            version.ShouldBe("TESTVERSION");
        }
        
        [TestMethod]
        public async Task BasicGensisTime()
        {
            // Arrange
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection();

            IBeaconNodeOApiClient beaconNodeOApiClient = Substitute.For<IBeaconNodeOApiClient>();
            beaconNodeOApiClient.TimeAsync(Arg.Any<CancellationToken>()).Returns(1_578_009_600uL);
            IBeaconNodeOApiClientFactory beaconNodeOApiClientFactory = Substitute.For<IBeaconNodeOApiClientFactory>();
            beaconNodeOApiClientFactory.CreateClient(Arg.Any<string>()).Returns(beaconNodeOApiClient);
            testServiceCollection.AddSingleton<IBeaconNodeOApiClientFactory>(beaconNodeOApiClientFactory);
            
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
            
            // Act
            IBeaconNodeApi beaconNodeProxy = testServiceProvider.GetService<IBeaconNodeApi>();
            beaconNodeProxy.ShouldBeOfType(typeof(BeaconNodeProxy));
            ulong genesisTime = await beaconNodeProxy.GetGenesisTimeAsync(CancellationToken.None);
            
            // Assert
            genesisTime.ShouldBe(1578009600uL);
        }

    }
}