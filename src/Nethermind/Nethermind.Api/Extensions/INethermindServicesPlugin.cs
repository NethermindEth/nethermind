using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Api.Extensions
{
    public interface INethermindServicesPlugin
    {
        void AddServices(IServiceCollection service);
    }
}
