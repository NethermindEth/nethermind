// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Services;
using Nethermind.Consensus;

namespace Nethermind.Merge.Plugin
{
    public class MergeHealthHintService : IHealthHintService
    {
        private readonly IHealthHintService _healthHintService;
        private readonly IPoSSwitcher _poSSwitcher;
        private readonly IMergeConfig _mergeConfig;

        public MergeHealthHintService(IHealthHintService? healthHintService, IPoSSwitcher? poSSwitcher, IMergeConfig? mergeConfig)
        {
            _healthHintService = healthHintService ?? throw new ArgumentNullException(nameof(healthHintService));
            _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
            _mergeConfig = mergeConfig ?? throw new ArgumentNullException(nameof(mergeConfig));
        }

        public ulong? MaxSecondsIntervalForProcessingBlocksHint()
        {
            if (_poSSwitcher.HasEverReachedTerminalBlock())
            {
                return _mergeConfig.SecondsPerSlot * 6;
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
