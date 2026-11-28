// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.SendPolicy.Plugin;
using Nethermind.Specs;
using Nethermind.TxPool;
using Testably.Abstractions;

namespace Nethermind.LightClient.Rpc;

internal sealed class SendPolicyRelay
{
    private readonly SendPolicyConfig _config;
    private readonly ulong _chainId;
    private readonly SendPolicyRuleFile _ruleFile;
    private readonly SendPolicyJournal _journal;
    private readonly EthereumEcdsa _ecdsa;
    private readonly ILogManager _logManager;

    internal SendPolicyRelay(string rulesPath, ulong chainId, ILogManager logManager)
    {
        _config = new SendPolicyConfig { Enabled = true, RulesPath = rulesPath };
        _chainId = chainId;
        _ruleFile = new SendPolicyRuleFile(_config, new RealFileSystem(), logManager);
        _journal = new SendPolicyJournal(_config, Timestamper.Default, logManager);
        _ecdsa = new EthereumEcdsa(chainId);
        _logManager = logManager;
    }

    internal async Task RelayAsync(MempoolTransaction transaction, IReleaseSpec spec, Func<MempoolTransaction, Task> relay)
    {
        SendPolicyTxSender sender = new(new RelayTxSender(relay), _ruleFile, _journal, _config, _ecdsa,
            new RelaySpecProvider(spec, _chainId), _logManager);
        (_, AcceptTxResult? result) = await sender.SendTransaction(transaction, TxHandlingOptions.None);
        if (result is { } refused && !refused) throw new RpcException(-32003, refused.ToString());
    }

    private sealed class RelaySpecProvider(IReleaseSpec spec, ulong chainId)
        : SingleReleaseSpecProvider(spec, chainId, chainId), IChainHeadSpecProvider
    {
        public IReleaseSpec GetCurrentHeadSpec() => GenesisSpec;
    }

    private sealed class RelayTxSender(Func<MempoolTransaction, Task> relay) : ITxSender
    {
        public async ValueTask<(Hash256 Hash, AcceptTxResult? AddTxResult)> SendTransaction(MempoolTransaction tx, TxHandlingOptions txHandlingOptions)
        {
            await relay(tx);
            return (tx.Hash!, AcceptTxResult.Accepted);
        }
    }
}
