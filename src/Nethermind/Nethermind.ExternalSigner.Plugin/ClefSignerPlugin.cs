// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Core;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc;
using Nethermind.Network;
using Nethermind.Consensus;
using Nethermind.KeyStore.Config;
using System.Configuration;

namespace Nethermind.ExternalSigner.Plugin;

public class ClefSignerPlugin : INethermindPlugin
{
    private INethermindApi? _nethermindApi;

    public string Name => "Clef signer";

    public string Description => "Enabled signing from a remote Clef instance over Json RPC.";

    public string Author => "Nethermind";

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public async Task Init(INethermindApi nethermindApi)
    {
        _nethermindApi = nethermindApi ?? throw new ArgumentNullException(nameof(nethermindApi));

        if (_nethermindApi == null)
            throw new InvalidOperationException("Init() must be called first.");

        IMiningConfig miningConfig = _nethermindApi.Config<IMiningConfig>();
        if (miningConfig.Enabled)
        {
            if (!string.IsNullOrEmpty(miningConfig.Signer))
            {
                Uri? uri;
                if (!Uri.TryCreate(miningConfig.Signer, UriKind.Absolute, out uri))
                {
                    throw new ConfigurationErrorsException($"{miningConfig.Signer} must have be a valid uri.");
                }
                ClefSigner signer =
                    await SetupExternalSigner(uri, _nethermindApi.Config<IKeyStoreConfig>().BlockAuthorAccount);
                _nethermindApi.EngineSigner = signer;
            }
        }

    }

    public Task InitNetworkProtocol()
    {
        return Task.CompletedTask;
    }

    public Task InitRpcModules()
    {
        return Task.CompletedTask;
    }

    private async Task<ClefSigner> SetupExternalSigner(Uri urlSigner, string blockAuthorAccount)
    {
        try
        {
            Address? address = string.IsNullOrEmpty(blockAuthorAccount) ? null : new Address(blockAuthorAccount);
            BasicJsonRpcClient rpcClient = new(urlSigner, _nethermindApi!.EthereumJsonSerializer, _nethermindApi.LogManager, TimeSpan.FromSeconds(10));
            _nethermindApi.DisposeStack.Push(rpcClient);
            return await ClefSigner.Create(rpcClient, address);
        }
        catch (HttpRequestException e)
        {
            throw new NetworkingException($"Remote signer at {urlSigner} did not respond.", NetworkExceptionType.TargetUnreachable, e);
        }
    }
}
