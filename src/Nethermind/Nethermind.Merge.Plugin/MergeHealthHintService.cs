// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Services;
using Nethermind.Config;
using Nethermind.Consensus;

namespace Nethermind.Merge.Plugin
{
    public class MergeHealthHintService : IHealthHintService
    {
        private readonly IHealthHintService _healthHintService;
        private readonly IPoSSwitcher _poSSwitcher;
        private readonly IBlocksConfig _blocksConfig;

        public MergeHealthHintService(IHealthHintService? healthHintService, IPoSSwitcher? poSSwitcher, IBlocksConfig blocksConfig)
        {
            _healthHintService = healthHintService ?? throw new ArgumentNullException(nameof(healthHintService));
            _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
            _blocksConfig = blocksConfig ?? throw new ArgumentNullException(nameof(blocksConfig));
        }

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
