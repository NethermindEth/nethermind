// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Core;
using Nethermind.TxPool;

namespace Nethermind.SendPolicy.Plugin;

public class SendPolicyPlugin(ISendPolicyConfig config) : INethermindPlugin
{
    public string Name => "SendPolicy";
    public string Description => "Refuses transactions submitted through this node's JSON-RPC that break the owner's rule file.";
    public string Author => "Nethermind";
    public bool Enabled => config.Enabled;
    public IModule Module => new SendPolicyModule();
}

public class SendPolicyModule : Module
{
    protected override void Load(ContainerBuilder builder) => builder
        .AddSingleton<SendPolicyRuleFile>()
        .AddSingleton<SendPolicyJournal>()
        .AddDecorator<ITxSender, SendPolicyTxSender>();
}
