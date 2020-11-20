using Microsoft.Extensions.DependencyInjection;
using Nethermind.Api;

namespace Nethermind.Services.Plugin
{
    public interface INethermindServicesPlugin
    {
        void AddServices(IServiceCollection service);
    }
}
