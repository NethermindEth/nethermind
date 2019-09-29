using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cortex.BeaconNode.Api
{
    public static class BeaconNodeServiceCollectionExtensions
    {
        public static void AddBeaconNodeApi(this IServiceCollection services)
        {
            // Register adapter
            services.AddScoped<IBeaconNodeApiController, BeaconNodeApiAdapter>();
            // Register controllers            
            var apiAssembly = typeof(BeaconNodeServiceCollectionExtensions).GetTypeInfo().Assembly;
            services.AddMvc().AddApplicationPart(apiAssembly);
        }
    }
}