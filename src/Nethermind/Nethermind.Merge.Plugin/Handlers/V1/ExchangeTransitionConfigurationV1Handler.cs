//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using Nethermind.Consensus;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data.V1;

namespace Nethermind.Merge.Plugin.Handlers.V1;

public class ExchangeTransitionConfigurationV1Handler : IHandler<TransitionConfigurationV1, TransitionConfigurationV1>
{
    private readonly IPoSSwitcher _poSSwitcher;
    private readonly ILogger _logger;

    public ExchangeTransitionConfigurationV1Handler(
        IPoSSwitcher poSSwitcher,
        ILogManager logManager)
    {
        _poSSwitcher = poSSwitcher;
        _logger = logManager.GetClassLogger();
    }
    
    public ResultWrapper<TransitionConfigurationV1> Handle(TransitionConfigurationV1 beaconTransitionConfiguration)
    {
        UInt256? terminalTotalDifficulty = _poSSwitcher.TerminalTotalDifficulty;
        long configuredTerminalBlockNumber = _poSSwitcher.ConfiguredTerminalBlockNumber ?? 0;
        Keccak configuredTerminalBlockHash = _poSSwitcher.ConfiguredTerminalBlockHash ?? Keccak.Zero;

        if (terminalTotalDifficulty == null)
        {
            if (_logger.IsWarn) _logger.Warn($"[MergeTransitionInfo] Terminal Total Difficulty wasn't specified in Nethermind");
        }
        if (beaconTransitionConfiguration.TerminalTotalDifficulty != terminalTotalDifficulty)
        {
            if (_logger.IsWarn) _logger.Warn($"[MergeTransitionInfo] Found the difference in terminal total difficulty between Nethermind and CL. Update your CL or Nethermind configuration. Nethermind TTD: {terminalTotalDifficulty}, CL TTD: {beaconTransitionConfiguration.TerminalTotalDifficulty}");
        }
        if (beaconTransitionConfiguration.TerminalBlockHash != configuredTerminalBlockHash)
        {
            if (_logger.IsWarn) _logger.Warn($"[MergeTransitionInfo] Found the difference in terminal block hash between Nethermind and CL. Update your CL or Nethermind configuration. Nethermind TerminalBlockHash: {configuredTerminalBlockHash}, CL TerminalBlockHash: {beaconTransitionConfiguration.TerminalBlockHash}");
        }
        
        return ResultWrapper<TransitionConfigurationV1>.Success(new TransitionConfigurationV1()
        {
            TerminalBlockHash = configuredTerminalBlockHash,
            TerminalBlockNumber = configuredTerminalBlockNumber,
            TerminalTotalDifficulty = terminalTotalDifficulty ?? 0
        });
    }
}
