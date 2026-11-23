// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.LightClient.Rpc;
using Nethermind.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Nethermind.LightClient.Eez;

internal sealed class EezLightClientExtension : IAsyncDisposable
{
    internal const string Help =
        "EEZ mode: --eez-genesis <geth genesis> --eez-genesis-hash <hash> --eez-chain-id <id> --eez-registry <0xaddress>\n" +
        "          --eez-rollup-id <id> --eez-bootnodes <enode,...> and/or --eez-dns <enrtree> [--eez-port 30308] [--eez-name rollupx]\n" +
        "          serves the rollup block committed in the EEZ registry at the finalized L1 head.\n";

    private readonly EezLightClientOptions _options;
    private readonly EezHeadPublisher _publisher;
    private readonly ILogger _logger;

    internal ExecutionPeerTransport Transport { get; }
    internal EezExecutionRules CallRules { get; }
    internal VerifiedHead? Head => _publisher.Current?.Head;
    internal Task NextHeadChange => _publisher.NextHeadChange;

    internal EezLightClientExtension(EezLightClientOptions options, ExecutionPeerTransport settlement, string dataDirectory,
        Address[] hostedAccounts, ulong settlementChainId, ILogManager logs, ILogger logger)
    {
        _options = options;
        _logger = logger;
        EezExecutionRules rules = EezExecutionRules.FromGenesis(options.Network.ChainSpecFile, settlementChainId);
        CallRules = rules.ForCalls();
        Transport = new(options.Network with { ExecutionRules = rules }, logs, dataDirectory: dataDirectory,
            hostedAccounts: hostedAccounts, port: options.Port);
        _publisher = new(settlement, Transport, options.Settlement, Transport.UpdateHead);
    }

    internal async Task StartAsync(CancellationToken token)
    {
        if (Transport.SpecProvider.ChainId != _options.Settlement.ChainId)
            throw new ArgumentException("--eez-chain-id does not match the rollup genesis.");
        await Transport.StartAsync(token);
        _logger.LogInformation("Rollup execution P2P started for {Network}", _options.Network.Name);
    }

    internal async Task RunAsync(Func<VerifiedHead?> getSettlementHead, Action<VerifiedHead> publishHead,
        TimeSpan interval, TimeProvider clock, CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(interval, clock);
        Hash256? lastPublishedHash = null;
        try
        {
            do
            {
                if (getSettlementHead() is not { } settlementHead) continue;
                try
                {
                    if (!await _publisher.UpdateAsync(settlementHead, cancellationToken)) continue;
                }
                catch (Exception exception) when (exception is ExecutionDataUnavailableException or RpcException)
                {
                    _logger.LogDebug("Waiting for EEZ rollup data: {Reason}", exception.Message);
                    continue;
                }
                catch (InvalidDataException exception)
                {
                    _logger.LogWarning(exception, "EEZ settlement resolution failed; retaining the last verified rollup head");
                    continue;
                }

                EezCommittedHead committed = _publisher.Current!;
                if (committed.Head.BlockHash != lastPublishedHash)
                {
                    publishHead(committed.Head);
                    lastPublishedHash = committed.Head.BlockHash;
                }
                _logger.LogInformation("EEZ rollup block {BlockNumber} committed at finalized L1 block {SettlementNumber}",
                    committed.Head.Number, committed.SettlementHead.Number);
            } while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    internal void ReportPeers() =>
        _logger.LogInformation("Rollup peers: execution {ExecutionPeers}, SNAP {SnapPeers} ({ExecutionCandidates} candidates)",
            Transport.PeerCount, Transport.SnapPeerCount, Transport.CandidateCount);

    public ValueTask DisposeAsync() => Transport.DisposeAsync();
}
