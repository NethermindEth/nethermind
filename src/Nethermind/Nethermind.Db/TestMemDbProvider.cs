// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System.Threading.Tasks;

namespace Nethermind.Db
{
    public class TestMemDbProvider
    {
        public static async Task<IDbProvider> InitAsync()
        {
            IDbProvider memDbProvider = new DbProvider(DbModeHint.Mem);
            StandardDbInitializer? standardDbInitializer = new StandardDbInitializer(memDbProvider, null, new MemDbFactory());
            await standardDbInitializer.InitStandardDbsAsync(true);
            return memDbProvider;
        }

        public static IDbProvider Init()
        {
            IDbProvider memDbProvider = new DbProvider(DbModeHint.Mem);
            StandardDbInitializer? standardDbInitializer = new StandardDbInitializer(memDbProvider, null, new MemDbFactory());
            standardDbInitializer.InitStandardDbs(true);
            return memDbProvider;
        }
    }
}
