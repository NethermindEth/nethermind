using Lantern.Discv5.Enr;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Messages;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Table;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lantern.Discv5.WireProtocol;

public interface IDiscv5ProtocolBuilder
{
    IDiscv5ProtocolBuilder WithConnectionOptions(ConnectionOptions connectionOptions);

    IDiscv5ProtocolBuilder WithSessionOptions(SessionOptions sessionOptions);

    IDiscv5ProtocolBuilder WithTableOptions(TableOptions tableOptions);

    IDiscv5ProtocolBuilder WithEnrBuilder(EnrBuilder enrBuilder);

    IDiscv5ProtocolBuilder WithTalkResponder(ITalkReqAndRespHandler talkResponder);

    IDiscv5ProtocolBuilder WithEnrEntryRegistry(IEnrEntryRegistry enrEntryRegistry);

    IDiscv5ProtocolBuilder WithLoggerFactory(ILoggerFactory loggerFactory);

    IDiscv5ProtocolBuilder WithConnectionOptions(Action<ConnectionOptions> configure);

    IDiscv5ProtocolBuilder WithSessionOptions(Action<SessionOptions> configure);

    IDiscv5ProtocolBuilder WithTableOptions(Action<TableOptions> configure);

    IDiscv5ProtocolBuilder WithEnrBuilder(Action<EnrBuilder> configure);

    IDiscv5ProtocolBuilder WithLoggerFactory(Action<ILoggerFactory> configure);

    IDiscv5ProtocolBuilder WithTalkResponder(Action<ITalkReqAndRespHandler> configure);

    IDiscv5ProtocolBuilder WithServices(Action<IServiceCollection> setup);

    IServiceProvider GetServiceProvider();

    IDiscv5Protocol Build();
}