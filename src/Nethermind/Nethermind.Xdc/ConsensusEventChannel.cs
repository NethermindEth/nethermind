// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using System.Threading.Channels;

namespace Nethermind.Xdc;

internal class ConsensusEventChannel
{
    private const int Capacity = 256;

    private readonly Channel<IConsensusEvent> _channel = Channel.CreateBounded<IConsensusEvent>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

    private readonly ILogger _logger;

    public ConsensusEventChannel(ILogManager logManager)
    {
        _logger = logManager.GetClassLogger<ConsensusEventChannel>();
    }

    public ChannelReader<IConsensusEvent> Reader => _channel.Reader;

    public void TryWrite(IConsensusEvent e)
    {
        if (!_channel.Writer.TryWrite(e))
        {
            if (_logger.IsWarn) _logger.Warn($"Consensus event channel is full, dropped {e.GetType().Name}.");
        }
    }
}
