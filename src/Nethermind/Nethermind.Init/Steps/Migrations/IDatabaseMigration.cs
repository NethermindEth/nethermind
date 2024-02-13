// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Init.Steps.Migrations
{
    public interface IDatabaseMigration
    {
        Task Run(CancellationToken cancellationToken);
    }
}
