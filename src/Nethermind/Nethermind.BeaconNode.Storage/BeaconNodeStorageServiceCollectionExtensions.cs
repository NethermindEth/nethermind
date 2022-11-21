// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
