using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.BeaconNode.OApi
{
    public static class BeaconNodeServiceCollectionExtensions
    {
        public static void AddBeaconNodeOApi(this IServiceCollection services)
        {
            // Register adapter
            services.AddScoped<IBeaconNodeOApiController, BeaconNodeOApiAdapter>();
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
