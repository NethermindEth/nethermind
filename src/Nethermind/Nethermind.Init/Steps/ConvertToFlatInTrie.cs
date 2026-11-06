// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;

namespace Nethermind.Init.Steps;

[StepCommand("convert-to-flat-in-trie", "Convert the flat state database from the Flat layout to FlatInTrie.")]
public class ConvertToFlatInTrie(FlatInTrieConverter converter, IFlatDbConfig flatDbConfig, ILogManager logManager) : IStep
{
    private readonly ILogger _logger = logManager.GetClassLogger<ConvertToFlatInTrie>();

    public async Task Execute(CancellationToken cancellationToken)
    {
        // The flat DB is opened with the RocksDB options of the configured layout, and the conversion compacts it into them.
        if (flatDbConfig.Layout != FlatLayout.FlatInTrie)
        {
            throw new InvalidConfigurationException(
                $"Cannot convert with FlatDb.Layout={flatDbConfig.Layout}. Set FlatDb.Layout={nameof(FlatLayout.FlatInTrie)} to convert to it.",
                ExitCodes.ForbiddenOptionValue);
        }

        try
        {
            await Task.Run(() => converter.Convert(cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (_logger.IsInfo) _logger.Info("Conversion cancelled by user");
        }
    }
}
