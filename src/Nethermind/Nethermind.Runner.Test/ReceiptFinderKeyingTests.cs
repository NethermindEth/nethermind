// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Receipts;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

/// <summary>
/// Enforces the receipt-finder keying invariant: the unkeyed <see cref="IReceiptFinder"/> is always stored-only,
/// and only read-only query paths may resolve <see cref="IReceiptFinder.RegenerableKey"/>.
/// </summary>
/// <remarks>
/// Both registrations satisfy the same interface, so a consumer that forgets the key compiles fine and silently
/// loses regeneration — exactly the bug found live on eth_getBlockReceipts and then again on parity/proof/OP-stack
/// receipts. This test walks every constructor in the shipped assemblies and fails on any
/// <see cref="IReceiptFinder"/> parameter that is neither keyed nor explicitly claimed below, so adding a consumer
/// forces a decision instead of defaulting to the wrong finder unnoticed.
/// </remarks>
[Parallelizable(ParallelScope.All)]
public class ReceiptFinderKeyingTests
{
    /// <summary>
    /// Consumers that must stay on the stored-only finder: consensus-path readers that tolerate absent receipts
    /// (regeneration throws instead), peer-facing serving, and monitoring — none of which may cost a block execution.
    /// </summary>
    private static readonly HashSet<string> StoredOnlyConsumers =
    [
        "Nethermind.Synchronization.SyncServer",
        "Nethermind.Runner.Monitoring.DataFeed",
        "Nethermind.Consensus.AuRa.AuRaValidatorFactory",
        "Nethermind.Consensus.AuRa.Validators.ContractBasedValidator",
        "Nethermind.Consensus.AuRa.InitializationSteps.TxAuRaFilterBuilders",
        "Nethermind.Shutter.ShutterApi",
        "Nethermind.Shutter.ShutterBlockHandler",
    ];

    /// <summary>
    /// Types that are not resolved from the container: they receive whatever their creator passes, so the keying
    /// decision is made (and checked) at the creator.
    /// </summary>
    private static readonly HashSet<string> NotContainerResolved =
    [
        "Nethermind.Consensus.Receipts.RegeneratingReceiptFinder",
        "Nethermind.Blockchain.Receipts.FullInfoReceiptFinder",
        "Nethermind.JsonRpc.Modules.Eth.EthRpcModule",
        "Nethermind.Optimism.Rpc.OptimismEthRpcModule",
        "Nethermind.JsonRpc.TraceStore.TraceStoreRpcModule",
        "Nethermind.JsonRpc.Modules.Trace.TraceRpcModule",
        "Nethermind.Consensus.AuRa.Contracts.DataStore.ContractDataStore",
        "Nethermind.Consensus.AuRa.Contracts.DataStore.ContractDataStoreWithLocalData",
        "Nethermind.Consensus.AuRa.Contracts.DataStore.DictionaryContractDataStore",
    ];

    [Test]
    public void Every_receipt_finder_constructor_parameter_is_keyed_or_claimed()
    {
        // AppDomain.GetAssemblies() only lists what is already loaded, which would silently skip plugins the test
        // never touches — load every shipped Nethermind assembly copied next to the test instead.
        Assembly[] assemblies = System.IO.Directory
            .EnumerateFiles(AppContext.BaseDirectory, "Nethermind.*.dll")
            .Select(System.IO.Path.GetFileNameWithoutExtension)
            .Where(name => name is not null && !name.Contains("Test", StringComparison.Ordinal))
            .Select(name => Assembly.Load(name!))
            .ToArray();
        Assert.That(assemblies, Has.Length.GreaterThan(20), "the scan must cover the full shipped assembly set");

        List<string> unclaimed = [];
        foreach (Assembly assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t is not null).ToArray()!;
            }

            foreach (Type type in types)
            {
                string name = StripGenericSuffix(type.FullName ?? type.Name);
                foreach (ConstructorInfo ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    foreach (ParameterInfo parameter in ctor.GetParameters())
                    {
                        if (parameter.ParameterType != typeof(IReceiptFinder)) continue;
                        if (parameter.GetCustomAttribute<KeyFilterAttribute>() is not null) continue;
                        if (StoredOnlyConsumers.Contains(name) || NotContainerResolved.Contains(name)) continue;

                        unclaimed.Add($"{name}({parameter.Name})");
                    }
                }
            }
        }

        Assert.That(unclaimed, Is.Empty,
            $"Unkeyed {nameof(IReceiptFinder)} resolves the stored-only finder and silently loses receipt " +
            $"regeneration. Add [KeyFilter({nameof(IReceiptFinder)}.{nameof(IReceiptFinder.RegenerableKey)})] for a " +
            "read-only query path, or claim the consumer in this test's stored-only list.");
    }

    private static string StripGenericSuffix(string name)
    {
        int backtick = name.IndexOf('`');
        return backtick < 0 ? name : name[..backtick];
    }
}
