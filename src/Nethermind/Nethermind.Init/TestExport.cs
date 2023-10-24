// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Init.Steps.Migrations;
using Nethermind.Era1;

namespace Nethermind.Init.Steps;
[RunnerStepDependencies(typeof(InitRlp), typeof(InitDatabase), typeof(InitializeBlockchain), typeof(InitializeNetwork), typeof(DatabaseMigrations))]
public sealed class EraExports : IStep
{
    private readonly IApiWithNetwork _api;

    public EraExports(INethermindApi api)
    {
        _api = api;
    }

    public Task Execute(CancellationToken cancellationToken)
    {
        var era = new EraService(new FileSystem());
         return era.Export("D:\\eraexport", "mainnet", _api.BlockTree!, _api.ReceiptStorage!, 0, 100_000, cancellationToken);
    }

    private IEnumerable<IDatabaseMigration> CreateMigrations()
    {
        yield return new BloomMigration(_api);
        yield return new ReceiptMigration(_api);
        yield return new ReceiptFixMigration(_api);
        yield return new TotalDifficultyFixMigration(_api.ChainLevelInfoRepository, _api.BlockTree, _api.Config<ISyncConfig>(), _api.LogManager);
    }
}
