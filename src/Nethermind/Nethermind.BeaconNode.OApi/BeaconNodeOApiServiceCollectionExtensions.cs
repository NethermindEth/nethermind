//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
