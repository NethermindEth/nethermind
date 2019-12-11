using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.BeaconNode.Api
{
    public static class BeaconNodeServiceCollectionExtensions
    {
        public static void AddBeaconNodeApi(this IServiceCollection services)
        {
            // Register adapter
            services.AddScoped<IBeaconNodeApiController, BeaconNodeApiAdapter>();
            // Register controllers
            var apiAssembly = typeof(BeaconNodeServiceCollectionExtensions).GetTypeInfo().Assembly;
            //services.AddControllers()
            //    .AddJsonOptions(options => { });
            services.AddMvc(setup =>
            {
                setup.ModelBinderProviders.Insert(0, new PrefixedHexByteArrayModelBinderProvider());
            })
                //.AddNewtonsoftJson(setup =>
                //{
                //    setup.SerializerSettings.Converters.Add(new ByteToPrefixedHexNewtonsoftJsonConverter());
                //})
                .AddJsonOptions(options =>
                {
                    options.JsonSerializerOptions.Converters.Add(new PrefixedHexByteArrayJsonConverter());
                })
                .AddApplicationPart(apiAssembly);
        }
    }
}
