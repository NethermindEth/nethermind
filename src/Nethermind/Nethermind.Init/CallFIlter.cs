// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autofac;
using Microsoft.AspNetCore.Mvc.Filters;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Extensions;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.State.OverridableEnv;
using Nethermind.TxPool;
using Nethermind.TxPool.Filters;
using ZstdSharp.Unsafe;

namespace Nethermind.Init;

public record CompliantNodeFilters(IReadOnlyCollection<IIncomingTxFilter> Filters);

public class CompliantNodeFilterFactory(
    ITxPoolConfig config,
    IOverridableEnvFactory envFactory,
    ILifetimeScope rootLifetimeScope,
    IBlockValidationModule[] validationBlockProcessingModules)
{

    private ContainerBuilder ConfigureTracerContainer(ContainerBuilder builder) =>
        builder
            // Standard configuration
            // Note: Not overriding `IReceiptStorage` to null.
            .AddModule(validationBlockProcessingModules)
            .AddDecorator<IBlockchainProcessor, OneTimeChainProcessor>()
            .AddScoped<BlockchainProcessor.Options>(BlockchainProcessor.Options.NoReceipts)

            // So the debug rpc change the adapter sometime.
            .AddScoped<ITransactionProcessorAdapter, ChangeableTransactionProcessorAdapter>();
    public CompliantNodeFilters Create()
    {
        IOverridableEnv env = envFactory.Create();
        ILifetimeScope tracerLifecyccle = rootLifetimeScope.BeginLifetimeScope((builder) =>
            ConfigureTracerContainer(builder)
                .AddModule(env));

        ILogManager logManager = tracerLifecyccle.Resolve<ILogManager>();
        List<IIncomingTxFilter> filters = new();
        var callFilter = new CallFilter(config, tracerLifecyccle.Resolve<IGethStyleTracer>(), logManager.GetClassLogger<TxPool.TxPool>());
        filters.Add(callFilter);
        var senderBlacklist = config.BlackListedSenderAddresses
            .Select(address => new AddressAsKey(new Address(address)))
            .ToHashSet();

        var receiverBlacklist = config.BlackListedReceiverAddresses
            .Select(address => new AddressAsKey(new Address(address)))
            .ToHashSet();

        if (senderBlacklist.Count > 0 || receiverBlacklist.Count > 0)
        {
            filters.Add(new AddressFilter(receiverBlacklist, senderBlacklist, logManager.GetClassLogger<TxPool.TxPool>()));
        }

        return new CompliantNodeFilters(filters);
    }
}

internal sealed class CallFilter:  IIncomingTxFilter
{
    private readonly Dictionary<AddressAsKey, HashSet<string>> _blacklistedFunctionCalls = new();
    private readonly IGethStyleTracer _gethStyleTracer;
    private readonly ILogger _logger;
    public CallFilter(ITxPoolConfig txPoolConfig, IGethStyleTracer bridge, ILogger logger)
    {
        logger.Info("Initializing Call Filter");
        foreach (var stuff in txPoolConfig.BlacklistedFunctionCalls)
        {
            var data = stuff.Split(';');
            _blacklistedFunctionCalls[new AddressAsKey(new Address(data[0]))] = new HashSet<string>(data[1..]);
            logger.Info($"{data[0]} {data[1]} {data[2]}");
        }
        _gethStyleTracer = bridge;
        _logger = logger;
    }
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        _logger.Info("We are in call tracer!");
        var options = new GethTraceOptions() { Tracer = NativeCallTracer.CallTracer };
        GethLikeTxTrace? trace = _gethStyleTracer.Trace(BlockParameter.Latest, tx, options, CancellationToken.None);

        EthereumJsonSerializer ser = new();

        var traces = (NativeCallTracerCallFrame)(trace!.CustomTracerResult!.Value);
        // _logger.Info(ser.Serialize(traces));
        if (_blacklistedFunctionCalls.Count != 0)
        {
            if (!IsFrameValid(_blacklistedFunctionCalls, traces) || traces.Calls.Any(tr => !IsFrameValid(_blacklistedFunctionCalls, traces)))
                return AcceptTxResult.BlacklistedAddress;
        }
        return AcceptTxResult.Accepted;
    }

    private static bool IsFrameValid(Dictionary<AddressAsKey, HashSet<string>> list, NativeCallTracerCallFrame frame)
    {
        if (list.TryGetValue(new AddressAsKey(frame.To!), out HashSet<string>? value))
        {
            var selector = frame.Input!.AsSpan()[..4];
            Console.WriteLine($"This is inside frame checker {frame.To} {selector.ToHexString()}");
            if (value.Contains(selector.ToHexString())) return false;
        }

        return true;
    }
}
