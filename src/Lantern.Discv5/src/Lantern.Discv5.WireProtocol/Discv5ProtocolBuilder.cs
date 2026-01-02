using Lantern.Discv5.Enr;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Logging;
using Lantern.Discv5.WireProtocol.Messages;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Table;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lantern.Discv5.WireProtocol;

public class Discv5ProtocolBuilder(IServiceCollection services) : IDiscv5ProtocolBuilder
{
    private ConnectionOptions _connectionOptions = new();
    private SessionOptions _sessionOptions = new();
    private TableOptions _tableOptions = new([]);
    private EnrBuilder _enrBuilder = new();
    private IEnrEntryRegistry _entryRegistry = new EnrEntryRegistry();
    private ILoggerFactory _loggerFactory = LoggingOptions.Default;
    private ITalkReqAndRespHandler? _talkResponder;
    private Action<IServiceCollection>? _servicesSetup;
    private IServiceProvider? _serviceProvider;

    public IDiscv5ProtocolBuilder WithConnectionOptions(ConnectionOptions connectionOptions)
    {
        _connectionOptions = connectionOptions ?? throw new ArgumentNullException(nameof(connectionOptions));
        return this;
    }

    public IDiscv5ProtocolBuilder WithConnectionOptions(Action<ConnectionOptions> configure)
    {
        configure(_connectionOptions);
        return this;
    }

    public IDiscv5ProtocolBuilder WithSessionOptions(SessionOptions sessionOptions)
    {
        _sessionOptions = sessionOptions ?? throw new ArgumentNullException(nameof(sessionOptions));
        return this;
    }

    public IDiscv5ProtocolBuilder WithSessionOptions(Action<SessionOptions> configure)
    {
        configure(_sessionOptions);
        return this;
    }

    public IDiscv5ProtocolBuilder WithTableOptions(TableOptions tableOptions)
    {
        _tableOptions = tableOptions ?? throw new ArgumentNullException(nameof(tableOptions));
        return this;
    }

    public IDiscv5ProtocolBuilder WithTableOptions(Action<TableOptions> configure)
    {
        configure(_tableOptions);
        return this;
    }

    public IDiscv5ProtocolBuilder WithEnrBuilder(EnrBuilder enrBuilder)
    {
        _enrBuilder = enrBuilder ?? throw new ArgumentNullException(nameof(enrBuilder));
        return this;
    }

    public IDiscv5ProtocolBuilder WithEnrBuilder(Action<EnrBuilder> configure)
    {
        configure(_enrBuilder);
        return this;
    }

    public IDiscv5ProtocolBuilder WithLoggerFactory(Action<ILoggerFactory> configure)
    {
        configure(_loggerFactory);
        return this;
    }

    public IDiscv5ProtocolBuilder WithTalkResponder(Action<ITalkReqAndRespHandler> configure)
    {
        configure(_talkResponder!);
        return this;
    }

    public IDiscv5ProtocolBuilder WithEnrEntryRegistry(IEnrEntryRegistry enrEntryRegistry)
    {
        _entryRegistry = enrEntryRegistry ?? throw new ArgumentNullException(nameof(enrEntryRegistry));
        return this;
    }

    public IDiscv5ProtocolBuilder WithTalkResponder(ITalkReqAndRespHandler talkResponder)
    {
        _talkResponder = talkResponder ?? throw new ArgumentNullException(nameof(talkResponder));
        return this;
    }

    public IDiscv5ProtocolBuilder WithLoggerFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        return this;
    }

    public IDiscv5ProtocolBuilder WithServices(Action<IServiceCollection> setup)
    {
        _servicesSetup = setup ?? throw new ArgumentNullException(nameof(setup));
        return this;
    }

    public IServiceProvider GetServiceProvider()
    {
        return _serviceProvider ?? throw new InvalidOperationException("Build() must be called before accessing the service provider.");
    }

    public IDiscv5Protocol Build()
    {
        services.AddDiscv5(_tableOptions, _connectionOptions, _sessionOptions, _entryRegistry, _enrBuilder.Build(), _loggerFactory, _talkResponder);
        _servicesSetup?.Invoke(services);
        _serviceProvider = services.BuildServiceProvider();
        return _serviceProvider.GetRequiredService<IDiscv5Protocol>();
    }
}
