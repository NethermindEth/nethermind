using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.Core2;
using NSubstitute;
using NSubstitute.Core;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Nethermind.BeaconNode.OApiClient.Test
{
    // [TestClass]
    // public class BeaconNodeProxyTest
    // {
    //     [TestMethod]
    //     public async Task BasicNodeVersion()
    //     {
    //         // Arrange
    //         IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection();
    //
    //         IBeaconNodeOApiClient beaconNodeOApiClient = Substitute.For<IBeaconNodeOApiClient>();
    //         beaconNodeOApiClient.VersionAsync(Arg.Any<CancellationToken>()).Returns("TESTVERSION");
    //         IBeaconNodeOApiClientFactory beaconNodeOApiClientFactory = Substitute.For<IBeaconNodeOApiClientFactory>();
    //         beaconNodeOApiClientFactory.CreateClient(Arg.Any<string>()).Returns(beaconNodeOApiClient);
    //         testServiceCollection.AddSingleton<IBeaconNodeOApiClientFactory>(beaconNodeOApiClientFactory);
    //         
    //         ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
    //         
    //         // Act
    //         IBeaconNodeApi beaconNodeProxy = testServiceProvider.GetService<IBeaconNodeApi>();
    //         beaconNodeProxy.ShouldBeOfType(typeof(BeaconNodeProxy));
    //         string version = await beaconNodeProxy.GetNodeVersionAsync(CancellationToken.None);
    //         
    //         // Assert
    //         version.ShouldBe("TESTVERSION");
    //     }
    //
    //     [TestMethod]
    //     public async Task NodeVersionTwiceShouldUseSameClient()
    //     {
    //         // Arrange
    //         IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection();
    //
    //         IBeaconNodeOApiClient beaconNodeOApiClient = Substitute.For<IBeaconNodeOApiClient>();
    //         beaconNodeOApiClient.VersionAsync(Arg.Any<CancellationToken>()).Returns("TESTVERSION");
    //         IBeaconNodeOApiClientFactory beaconNodeOApiClientFactory = Substitute.For<IBeaconNodeOApiClientFactory>();
    //         beaconNodeOApiClientFactory.CreateClient(Arg.Any<string>()).Returns(beaconNodeOApiClient);
    //         testServiceCollection.AddSingleton<IBeaconNodeOApiClientFactory>(beaconNodeOApiClientFactory);
    //         
    //         ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
    //         
    //         // Act
    //         IBeaconNodeApi beaconNodeProxy = testServiceProvider.GetService<IBeaconNodeApi>();
    //         beaconNodeProxy.ShouldBeOfType(typeof(BeaconNodeProxy));
    //         string version1 = await beaconNodeProxy.GetNodeVersionAsync(CancellationToken.None);
    //         string version2 = await beaconNodeProxy.GetNodeVersionAsync(CancellationToken.None);
    //         
    //         // Assert
    //         version1.ShouldBe("TESTVERSION");
    //         version2.ShouldBe("TESTVERSION");
    //         beaconNodeOApiClientFactory.CreateClient(Arg.Any<string>()).Received(1);
    //     }
    //
    //     [TestMethod]
    //     public async Task NodeVersionInitialFailTryAgain()
    //     {
    //         // Arrange
    //         IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection();
    //
    //         IBeaconNodeOApiClient beaconNodeOApiClient1 = Substitute.For<IBeaconNodeOApiClient>();
    //         beaconNodeOApiClient1.VersionAsync(Arg.Any<CancellationToken>()).Throws(new HttpRequestException("TESTEXCEPTION"));
    //         IBeaconNodeOApiClient beaconNodeOApiClient2 = Substitute.For<IBeaconNodeOApiClient>();
    //         beaconNodeOApiClient2.VersionAsync(Arg.Any<CancellationToken>()).Returns("TESTVERSION");
    //         IBeaconNodeOApiClientFactory beaconNodeOApiClientFactory = Substitute.For<IBeaconNodeOApiClientFactory>();
    //         beaconNodeOApiClientFactory.CreateClient(Arg.Any<string>()).Returns(beaconNodeOApiClient1, beaconNodeOApiClient2);
    //         testServiceCollection.AddSingleton<IBeaconNodeOApiClientFactory>(beaconNodeOApiClientFactory);
    //         
    //         ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
    //         
    //         // Act
    //         IBeaconNodeApi beaconNodeProxy = testServiceProvider.GetService<IBeaconNodeApi>();
    //         beaconNodeProxy.ShouldBeOfType(typeof(BeaconNodeProxy));
    //         string version1 = await beaconNodeProxy.GetNodeVersionAsync(CancellationToken.None);
    //         
    //         // Assert
    //         version1.ShouldBe("TESTVERSION");
    //         beaconNodeOApiClientFactory.CreateClient(Arg.Any<string>()).Received(2);
    //     }
    //     
    //     [TestMethod]
    //     public async Task NodeVersionInitialFailAfterSuccessTryAgain()
    //     {
    //         // Arrange
    //         IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection();
    //
    //         IBeaconNodeOApiClient beaconNodeOApiClient1 = Substitute.For<IBeaconNodeOApiClient>();
    //         beaconNodeOApiClient1.VersionAsync(Arg.Any<CancellationToken>()).Returns(
    //             callInfo => "TESTVERSION1", 
    //             callInfo => throw new HttpRequestException("TESTEXCEPTION")
    //         );
    //         beaconNodeOApiClient1.BaseUrl.Returns("CLIENT1");
    //         IBeaconNodeOApiClient beaconNodeOApiClient2 = Substitute.For<IBeaconNodeOApiClient>();
    //         beaconNodeOApiClient2.VersionAsync(Arg.Any<CancellationToken>()).Returns("TESTVERSION2");
    //         IBeaconNodeOApiClientFactory beaconNodeOApiClientFactory = Substitute.For<IBeaconNodeOApiClientFactory>();
    //         beaconNodeOApiClientFactory.CreateClient(Arg.Any<string>()).Returns(beaconNodeOApiClient1, beaconNodeOApiClient2);
    //         testServiceCollection.AddSingleton<IBeaconNodeOApiClientFactory>(beaconNodeOApiClientFactory);
    //         
    //         ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
    //         
    //         // Act
    //         IBeaconNodeApi beaconNodeProxy = testServiceProvider.GetService<IBeaconNodeApi>();
    //         beaconNodeProxy.ShouldBeOfType(typeof(BeaconNodeProxy));
    //         string version1 = await beaconNodeProxy.GetNodeVersionAsync(CancellationToken.None);
    //         string version2 = await beaconNodeProxy.GetNodeVersionAsync(CancellationToken.None);
    //         
    //         // Assert
    //         version1.ShouldBe("TESTVERSION1");
    //         version2.ShouldBe("TESTVERSION2");
    //         beaconNodeOApiClientFactory.CreateClient(Arg.Any<string>()).Received(2);
    //
    //         List<ICall> client1Received = beaconNodeOApiClient1.ReceivedCalls().ToList();
    //         client1Received.Count(x => x.GetMethodInfo().Name == nameof(beaconNodeOApiClient1.VersionAsync)).ShouldBe(2);
    //
    //         List<ICall> client2Received = beaconNodeOApiClient2.ReceivedCalls().ToList();
    //         client2Received.Count(x => x.GetMethodInfo().Name == nameof(beaconNodeOApiClient1.VersionAsync)).ShouldBe(1);
    //     }
    //     
    //     [TestMethod]
    //     public async Task BasicGensisTime()
    //     {
    //         // Arrange
    //         IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection();
    //
    //         IBeaconNodeOApiClient beaconNodeOApiClient = Substitute.For<IBeaconNodeOApiClient>();
    //         beaconNodeOApiClient.TimeAsync(Arg.Any<CancellationToken>()).Returns(1_578_009_600uL);
    //         IBeaconNodeOApiClientFactory beaconNodeOApiClientFactory = Substitute.For<IBeaconNodeOApiClientFactory>();
    //         beaconNodeOApiClientFactory.CreateClient(Arg.Any<string>()).Returns(beaconNodeOApiClient);
    //         testServiceCollection.AddSingleton<IBeaconNodeOApiClientFactory>(beaconNodeOApiClientFactory);
    //         
    //         ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
    //         
    //         // Act
    //         IBeaconNodeApi beaconNodeProxy = testServiceProvider.GetService<IBeaconNodeApi>();
    //         beaconNodeProxy.ShouldBeOfType(typeof(BeaconNodeProxy));
    //         ulong genesisTime = await beaconNodeProxy.GetGenesisTimeAsync(CancellationToken.None);
    //         
    //         // Assert
    //         genesisTime.ShouldBe(1578009600uL);
    //     }
    // }
}