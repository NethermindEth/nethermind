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

using System;
using System.IO.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nethermind.Core2;

namespace Nethermind.BeaconNode.Storage
{
    public static class BeaconNodeStorageServiceCollectionExtensions
    {
        public static void AddBeaconNodeStorage(this IServiceCollection services, IConfiguration configuration)
        {
            if (configuration.GetSection("Storage:InMemory").Exists())
            {
                services.Configure<InMemoryConfiguration>(x => configuration.Bind("Storage:InMemory", x));
                services.AddSingleton<IStore, MemoryStore>();
                services.AddSingleton<IHeadSelectionStrategy, SimpleLatestMessageDrivenGreedyHeaviestObservedSubtree>();
                services.AddSingleton<StoreAccessor>();
                services.TryAddTransient<IFileSystem, FileSystem>();
            }
            else
            {
                throw new Exception("No storage configuration found.");
            }
        }
    }
}