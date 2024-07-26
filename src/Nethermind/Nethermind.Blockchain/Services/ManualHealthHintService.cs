using System;

namespace Nethermind.Blockchain.Services;

public class ManualHealthHintService : IHealthHintService
{
    private readonly ulong? _maxSecondsIntervalForProcessingBlocksHint;
    private readonly ulong? _maxSecondsIntervalForProducingBlocksHint;

    public ManualHealthHintService(ulong? maxSecsIntervalForProcessingBlocksHint, ulong? maxSecsIntervalForProducingBlocksHint)
    {
        _maxSecondsIntervalForProcessingBlocksHint = maxSecsIntervalForProcessingBlocksHint;
        _maxSecondsIntervalForProducingBlocksHint = maxSecsIntervalForProducingBlocksHint;
    }

    public ulong? MaxSecondsIntervalForProcessingBlocksHint() => _maxSecondsIntervalForProcessingBlocksHint;

    public ulong? MaxSecondsIntervalForProducingBlocksHint() => _maxSecondsIntervalForProducingBlocksHint;
}
