// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Db;

namespace Nethermind.Api
{
    public static class BasicApiExtensions
    {
        public static T Config<T>(this IBasicApi api) where T : IConfig
        {
            return api.ConfigProvider.GetConfig<T>();
        }

        public static T Db<T>(this IBasicApi api, string dbName) where T : class, IDb
        {
            return api.DbProvider!.GetDb<T>(dbName);
        }
    }
}
