// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Autofac;
using Autofac.Core;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Follower.RollupX;

namespace Nethermind.Eez;

public partial class EezModule
{
    internal const string NativeL1ClientKey = "Eez.NativeL1";

    private void LoadNativeFollower(ContainerBuilder builder)
    {
        builder
            .AddSingleton<NativeL1Rules>(_ => NativeL1Rules.LoadEthereum(File.ReadAllBytes(config.L1ChainConfigPath!)))
            .AddKeyedSingleton<HttpClient>(NativeL1ClientKey, _ => NativeL1Client(config.L1RpcUrl!))
            .AddSingleton<NativeCanonicalHistory>()
            .Bind<IEezL1History, NativeCanonicalHistory>()
            .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockValidationTransactionsExecutor>()
            .AddDecorator<IBlockProcessor.IBlockTransactionsExecutor, NativeNodeHistoryExecutor>();
        builder.RegisterType<NativeL1Rpc>().WithParameter(ResolvedParameter.ForKeyed<HttpClient>(NativeL1ClientKey)).SingleInstance();
    }

    private static HttpClient NativeL1Client(string url)
    {
        Uri uri = new(url);
        UriBuilder endpoint = new(uri) { UserName = "", Password = "" };
        HttpClient client = new() { BaseAddress = endpoint.Uri };
        if (uri.UserInfo.Length != 0)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(uri.UserInfo))));
        return client;
    }
}
