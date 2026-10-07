// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
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
        .AddDecorator<ITxSender, SendPolicyTxSender>()
        .AddStep(typeof(LoadSendPolicyRules));
}

/// <summary>
/// Stops the node at startup when the rule file is unusable.
/// </summary>
/// <remarks>
/// Without it the file is first read when a JSON-RPC module is created, which is after the node has started.
/// </remarks>
[RunnerStepDependencies]
public class LoadSendPolicyRules(SendPolicyRuleFile ruleFile) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        _ = ruleFile;
        return Task.CompletedTask;
    }
}
