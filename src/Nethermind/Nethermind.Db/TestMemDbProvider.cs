// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Db
{
    public class TestMemDbProvider
    {
        public static async Task<IDbProvider> InitAsync()
        {
            IDbProvider memDbProvider = new DbProvider(DbModeHint.Mem);
            StandardDbInitializer? standardDbInitializer = new StandardDbInitializer(memDbProvider, null, new MemDbFactory(), LimboLogs.Instance);
            await standardDbInitializer.InitStandardDbsAsync(true);
            return memDbProvider;
        }

        public static IDbProvider Init()
        {
            IDbProvider memDbProvider = new DbProvider(DbModeHint.Mem);
            StandardDbInitializer? standardDbInitializer = new StandardDbInitializer(memDbProvider, null, new MemDbFactory(), LimboLogs.Instance);
            standardDbInitializer.InitStandardDbs(true);
            return memDbProvider;
        }
    }
}
