// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

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
