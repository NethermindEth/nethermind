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
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nethermind.Core2.Json;

namespace Nethermind.BeaconNode.OApi
{
    public static class BeaconNodeServiceCollectionExtensions
    {
        public static void AddBeaconNodeOApi(this IServiceCollection services, IWebHostEnvironment env)
        {
            // Register controllers
            var apiAssembly = typeof(BeaconNodeServiceCollectionExtensions).GetTypeInfo().Assembly;
            services.AddControllers()
                .AddMvcOptions(mvcOptions =>
                {
                    mvcOptions.ModelBinderProviders.Insert(0, new PrefixedHexByteArrayModelBinderProvider());
                })
                .AddJsonOptions(jsonOptions =>
                {
                    if (env.IsDevelopment())
                    {
                        jsonOptions.JsonSerializerOptions.WriteIndented = true;
                    }
                    
                    jsonOptions.JsonSerializerOptions.ConfigureNethermindCore2();
                })
                .AddApplicationPart(apiAssembly);
        }
    }
}
