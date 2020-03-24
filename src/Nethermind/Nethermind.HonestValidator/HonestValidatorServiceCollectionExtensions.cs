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

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Types;
using Nethermind.HonestValidator.Services;

namespace Nethermind.HonestValidator
{
    public static class HonestValidatorServiceCollectionExtensions
    {
        public static void AddHonestValidator(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddSingleton<IClock, SystemClock>();
            services.AddSingleton<BeaconChainInformation>();
            services.AddSingleton<ValidatorClient>();
            
            services.AddHostedService<HonestValidatorWorker>();
        }
    }
}
