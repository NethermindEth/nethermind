// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Services;
using Nethermind.Config;
using Nethermind.Consensus;

namespace Nethermind.Merge.Plugin
{
    public class MergeHealthHintService(IHealthHintService? healthHintService, IPoSSwitcher? poSSwitcher, IBlocksConfig blocksConfig) : IHealthHintService
    {
        private readonly IHealthHintService _healthHintService = healthHintService ?? throw new ArgumentNullException(nameof(healthHintService));
        private readonly IPoSSwitcher _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
        private readonly IBlocksConfig _blocksConfig = blocksConfig ?? throw new ArgumentNullException(nameof(blocksConfig));

        public ulong? MaxSecondsIntervalForProcessingBlocksHint()
        {
            if (_poSSwitcher.HasEverReachedTerminalBlock())
            {
                return _blocksConfig.SecondsPerSlot * 6;
            }

            return _healthHintService.MaxSecondsIntervalForProcessingBlocksHint();
        }

        public ulong? MaxSecondsIntervalForProducingBlocksHint()
        {
            if (_poSSwitcher.HasEverReachedTerminalBlock())
            {
                return long.MaxValue;
            }

            return _healthHintService.MaxSecondsIntervalForProducingBlocksHint();
        }
    }
}
