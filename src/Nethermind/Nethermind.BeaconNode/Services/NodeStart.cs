// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.Core2.Configuration;

namespace Nethermind.BeaconNode.Services
{
    public class NodeStart : INodeStart
    {
        private readonly IOptionsMonitor<AnchorState> _anchorStateOptions;
        private readonly ILogger<NodeStart> _logger;

        public NodeStart(ILogger<NodeStart> logger, IOptionsMonitor<AnchorState> anchorStateOptions)
        {
            _logger = logger;
            _anchorStateOptions = anchorStateOptions;
        }

        public Task InitializeNodeAsync()
        {
            AnchorState anchorState = _anchorStateOptions.CurrentValue;

            if (anchorState.Source == AnchorStateSource.Eth1Genesis)
            {
                // Do nothing -- anchor state is provided by the Eth1 bridge
            }
            else
            {
                throw new Exception($"Unknown anchor state source: '{anchorState.Source}'");
            }

            return Task.CompletedTask;
        }
    }
}
