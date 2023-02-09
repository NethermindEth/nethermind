// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Init.Steps.Migrations
{
    public interface IDatabaseMigration : IAsyncDisposable
    {
        void Run();
    }
}
