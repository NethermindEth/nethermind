// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.WriteTheDocs
{
    static class Program
    {
        static void Main(string[] args)
        {
            new ConfigDocsGenerator().Generate();
            new CliDocsGenerator().Generate();
            new RpcDocsGenerator().Generate();
            new MetricsDocsGenerator().Generate();
        }
    }
}
