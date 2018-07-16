/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nethermind.Runner
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddMvc().SetCompatibilityVersion(CompatibilityVersion.Version_2_1);

            //services.Configure<ApiBehaviorOptions>(options =>
            //{
            //    options.SuppressConsumesConstraintForFormFileParameters = true;
            //    options.SuppressInferBindingSourcesForParameters = true;
            //    options.SuppressModelStateInvalidFilter = true;
            //});

            Bootstrap.Instance.RegisterJsonRpcServices(services);

            //register our clases

            //if (Bootstrap.ServiceCollection == null)
            //{
            //    throw new Exception("Bootstrap.ServiceCollection was not properly initialized");
            //}

            //Bootstrap.ServiceCollection.Add(services);
            //Bootstrap.ServiceCollection.AddMvc();

            //Bootstrap.ServiceProvider = Bootstrap.ServiceCollection.BuildServiceProvider();
            //return Bootstrap.ServiceProvider;
        }       

        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            //app.UseHsts();
            //app.UseHttpsRedirection();
            app.UseMvc();
        }
    }
}
