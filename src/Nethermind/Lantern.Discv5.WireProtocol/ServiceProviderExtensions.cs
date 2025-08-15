using Microsoft.Extensions.DependencyInjection;

namespace Lantern.Discv5.WireProtocol;

public static class ServiceProviderExtensions
{
    public static IDiscv5ProtocolBuilder AddDiscv5(this IServiceCollection services, Action<IDiscv5ProtocolBuilder> configure)
    {
        var builder = new Discv5ProtocolBuilder(services);
        configure(builder);
        builder.Build();
        return builder;
    }
}