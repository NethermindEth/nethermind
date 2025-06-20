// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Core;
using Nethermind.JsonRpc.Client;
using Nethermind.Network;
using Nethermind.Consensus;
using Nethermind.KeyStore.Config;
using System.Configuration;
using Nethermind.Wallet;
using Nethermind.Serialization.Json;

namespace Nethermind.ExternalSigner.Plugin;

public class ClefSignerPlugin(IMiningConfig miningConfig) : INethermindPlugin
{
    private INethermindApi? _nethermindApi;

    public string Name => "Clef signer";

    public string Description => "Enabled signing from a remote Clef instance over Json RPC.";

    public string Author => "Nethermind";

    public bool MustInitialize => true;
    public bool Enabled => !string.IsNullOrEmpty(miningConfig.Signer);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task Init(INethermindApi nethermindApi)
    {
        _nethermindApi = nethermindApi ?? throw new ArgumentNullException(nameof(nethermindApi));
        if (!string.IsNullOrEmpty(miningConfig.Signer))
        {
            if (!Uri.TryCreate(miningConfig.Signer, UriKind.Absolute, out Uri? uri))
            {
                throw new ConfigurationErrorsException($"{miningConfig.Signer} must be a valid uri.");
            }

            string blockAuthorAccount = _nethermindApi.Config<IKeyStoreConfig>().BlockAuthorAccount;

            IJsonSerializer ethereumJsonSerializer = new EthereumJsonSerializer(new[] { new ChecksumAddressConverter() });

            BasicJsonRpcClient rpcClient = new(uri, ethereumJsonSerializer, _nethermindApi.LogManager, TimeSpan.FromSeconds(10));
            _nethermindApi.DisposeStack.Push(rpcClient);

            ClefWallet clefWallet = new(rpcClient);
            _nethermindApi.Wallet = clefWallet;

            if (miningConfig.Enabled)
                _nethermindApi.EngineSigner = SetupExternalSigner(clefWallet, blockAuthorAccount);

        }
        return Task.CompletedTask;
    }

    public Task InitNetworkProtocol() => Task.CompletedTask;

    public Task InitRpcModules() => Task.CompletedTask;

    private ClefSigner SetupExternalSigner(ClefWallet clefWallet, string blockAuthorAccount)
    {
        try
        {
            Address? address = string.IsNullOrEmpty(blockAuthorAccount) ? null : new Address(blockAuthorAccount);

            return ClefSigner.Create(clefWallet, address);
        }
        catch (HttpRequestException e)
        {
            throw new NetworkingException($"Remote signer did not respond during setup.", NetworkExceptionType.TargetUnreachable, e);
        }
    }
}
