namespace Nethermind.Blockchain.Services;

public class ManualHealthHintService(ulong? maxSecsIntervalForProcessingBlocksHint, ulong? maxSecsIntervalForProducingBlocksHint) : IHealthHintService
{
    private readonly ulong? _maxSecondsIntervalForProcessingBlocksHint = maxSecsIntervalForProcessingBlocksHint;
    private readonly ulong? _maxSecondsIntervalForProducingBlocksHint = maxSecsIntervalForProducingBlocksHint;

    public ulong? MaxSecondsIntervalForProcessingBlocksHint() => _maxSecondsIntervalForProcessingBlocksHint;

    public ulong? MaxSecondsIntervalForProducingBlocksHint() => _maxSecondsIntervalForProducingBlocksHint;
}
