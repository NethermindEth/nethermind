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

using System;
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

    // https://github.com/ethereum/consensus-specs/blob/981b05afb01d5b19be3a5a60ccb12c3582e4c0cf/configs/mainnet.yaml#L16
    private static readonly UInt256 _ttdPlaceholderForCl = UInt256.Parse("115792089237316195423570985008687907853269984665640564039457584007913129638912");

    public ExchangeTransitionConfigurationV1Handler(
        IPoSSwitcher poSSwitcher,
        ILogManager logManager)
    {
        _poSSwitcher = poSSwitcher;
        _logger = logManager.GetClassLogger();
    }

    public ResultWrapper<TransitionConfigurationV1> Handle(TransitionConfigurationV1 beaconTransitionConfiguration)
    {
        UInt256 terminalTotalDifficulty = _poSSwitcher.TerminalTotalDifficulty ?? _ttdPlaceholderForCl;
        long configuredTerminalBlockNumber = _poSSwitcher.ConfiguredTerminalBlockNumber ?? 0;
        Keccak configuredTerminalBlockHash = _poSSwitcher.ConfiguredTerminalBlockHash ?? Keccak.Zero;

        if (terminalTotalDifficulty == _ttdPlaceholderForCl)
        {
            if (_logger.IsTrace) _logger.Trace($"[MergeTransitionInfo] Terminal Total Difficulty wasn't specified in Nethermind. If TTD has already been announced you should set it in your Nethermind and Consensus Client configuration.");
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
            TerminalTotalDifficulty = terminalTotalDifficulty
        });
    }
}
