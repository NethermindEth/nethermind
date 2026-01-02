using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Messages;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Table;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Lantern.Discv5.WireProtocol.Tests;

[TestFixture]
public class Discv5ProtocolBuilderTests
{
    private IDiscv5ProtocolBuilder _builder = null!;

    [Test]
    public void WithSessionOptions_ChainsCorrectly_ReturnsBuilderInstance()
    {
        _builder = new Discv5ProtocolBuilder(new ServiceCollection());
        var returnedBuilder = _builder.WithSessionOptions(new SessionOptions());

        Assert.AreSame(_builder, returnedBuilder, "Method chaining should return the same builder instance.");
    }
    [Test]
    public void WithTableOptions_ChainsCorrectly_ReturnsBuilderInstance()
    {
        _builder = new Discv5ProtocolBuilder(new ServiceCollection());
        var returnedBuilder = _builder.WithTableOptions(new TableOptions([]));

        Assert.AreSame(_builder, returnedBuilder, "Method chaining should return the same builder instance.");
    }

    [Test]
    public void WithConnectionOptions_ChainsCorrectly_ReturnsBuilderInstance()
    {
        _builder = new Discv5ProtocolBuilder(new ServiceCollection());
        var returnedBuilder = _builder.WithConnectionOptions(new ConnectionOptions());

        Assert.AreSame(_builder, returnedBuilder, "Method chaining should return the same builder instance.");
    }

    [Test]
    public void WithConnectionOptions_ActionOverload_ChainsCorrectly_ReturnsBuilderInstance()
    {
        _builder = new Discv5ProtocolBuilder(new ServiceCollection());
        var returnedBuilder = _builder.WithConnectionOptions(options =>
        {
            options.UdpPort = 30303;
        });

        Assert.AreSame(_builder, returnedBuilder, "Method chaining should return the same builder instance.");
    }

    [Test]
    public void WithSessionOptions_ActionOverload_ChainsCorrectly_ReturnsBuilderInstance()
    {
        _builder = new Discv5ProtocolBuilder(new ServiceCollection());
        var returnedBuilder = _builder.WithSessionOptions(options =>
        {
            options.Verifier = SessionOptions.Default.Verifier;
        });

        Assert.AreSame(_builder, returnedBuilder, "Method chaining should return the same builder instance.");
    }

    [Test]
    public void WithEnrBuilder_ChainsCorrectly_ReturnsBuilderInstance()
    {
        _builder = new Discv5ProtocolBuilder(new ServiceCollection());
        var enrBuilder = new EnrBuilder();
        var returnedBuilder = _builder.WithEnrBuilder(enrBuilder);

        Assert.AreSame(_builder, returnedBuilder, "Method chaining should return the same builder instance.");
    }

    [Test]
    public void WithEnrBuilder_ActionOverload_ChainsCorrectly_ReturnsBuilderInstance()
    {
        _builder = new Discv5ProtocolBuilder(new ServiceCollection());
        var returnedBuilder = _builder.WithEnrBuilder(enrBuilder =>
        {
            enrBuilder.WithEntry(EnrEntryKey.Id, new EntryId("v4"));
        });

        Assert.AreSame(_builder, returnedBuilder, "Method chaining should return the same builder instance.");
    }

    [Test]
    public void WithTableOptions_ActionOverload_ChainsCorrectly_ReturnsBuilderInstance()
    {
        _builder = new Discv5ProtocolBuilder(new ServiceCollection());
        var returnedBuilder = _builder.WithTableOptions(options =>
        {
            options.BootstrapEnrs = ["enr:-example"];
        });

        Assert.AreSame(_builder, returnedBuilder, "Method chaining should return the same builder instance.");
    }

    [Test]
    public void WithLoggerFactory_ChainsCorrectly_ReturnsBuilderInstance()
    {
        _builder = new Discv5ProtocolBuilder(new ServiceCollection());
        var loggerFactory = new LoggerFactory();
        var returnedBuilder = _builder.WithLoggerFactory(loggerFactory);

        Assert.AreSame(_builder, returnedBuilder, "Method chaining should return the same builder instance.");
    }

    [Test]
    public void WithEnrEntryRegistry_ChainsCorrectly_ReturnsBuilderInstance()
    {
        _builder = new Discv5ProtocolBuilder(new ServiceCollection());
        var entryRegistry = new EnrEntryRegistry();
        var returnedBuilder = _builder.WithEnrEntryRegistry(entryRegistry);

        Assert.AreSame(_builder, returnedBuilder, "Method chaining should return the same builder instance.");
    }

    [Test]
    public void WithTalkResponder_ChainsCorrectly_ReturnsBuilderInstance()
    {
        _builder = new Discv5ProtocolBuilder(new ServiceCollection());
        var talkResponder = Mock.Of<ITalkReqAndRespHandler>();
        var returnedBuilder = _builder.WithTalkResponder(talkResponder);

        Assert.AreSame(_builder, returnedBuilder, "Method chaining should return the same builder instance.");
    }

    [Test]
    public void Build_WithConfigurations_ReturnsConfiguredInstance()
    {
        var services = new ServiceCollection();
        var discv5Builder = new Discv5ProtocolBuilder(services);
        var discv5Protocol = AddMandatoryConfigurations(discv5Builder).Build();

        Assert.IsNotNull(discv5Protocol, "Expected to return a configured instance.");
    }

    [Test]
    public void Build_WithServiceOverrides_UsesOverrides()
    {
        var services = new ServiceCollection();
        var discv5Builder = new Discv5ProtocolBuilder(services);

        var serviceOverride = Mock.Of<IConnectionManager>();
        AddMandatoryConfigurations(discv5Builder, 30304)
            .WithServices(s => s.AddSingleton(serviceOverride))
            .Build();

        var serviceProvider = discv5Builder.GetServiceProvider();
        var serviceResolved = serviceProvider.GetRequiredService<IConnectionManager>();

        Assert.AreSame(serviceOverride, serviceResolved, "Expected to return overriden service.");
    }

    [Test]
    public void Build_WithoutMandatoryConfigurations_ThrowsException()
    {
        _builder = new Discv5ProtocolBuilder(new ServiceCollection());
        Assert.Throws<InvalidOperationException>(() => _builder.Build(), "Expected to throw due to missing configurations.");
    }

    private static IDiscv5ProtocolBuilder AddMandatoryConfigurations(IDiscv5ProtocolBuilder discv5Builder, int udpPort = 30303)
    {
        string[] bootstrapEnrs = ["enr:-example"];
        var sessionOptions = SessionOptions.Default;
        var enr = new EnrBuilder()
            .WithIdentityScheme(sessionOptions.Verifier, sessionOptions.Signer)
            .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
            .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(sessionOptions.Signer.PublicKey));

        return discv5Builder
            .WithConnectionOptions(new ConnectionOptions { UdpPort = udpPort })
            .WithTableOptions(new TableOptions(bootstrapEnrs))
            .WithLoggerFactory(NullLoggerFactory.Instance)
            .WithEnrBuilder(enr);
    }
}
