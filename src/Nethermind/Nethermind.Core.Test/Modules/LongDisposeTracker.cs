// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Autofac.Core.Resolving.Pipeline;
using NUnit.Framework;

namespace Nethermind.Core.Test.Modules;

/// <summary>
/// Add a middleware that tracks the dispose time of disposable service.
/// If a service took more than 1 second to dispose, it will print a log.
/// Note: This causes double dispose
/// </summary>
public class LongDisposeTracker : IResolveMiddleware
{
    /// <summary>
    /// Need to be call earliest as it track the configuration calls..
    /// </summary>
    /// <param name="builder"></param>
    public static void Configure(ContainerBuilder builder)
    {
        builder.ComponentRegistryBuilder.Registered += (sender, args) =>
        {
            args.ComponentRegistration.ConfigurePipeline((pipeline) =>
            {
                pipeline.Use(Instance, MiddlewareInsertionMode.EndOfPhase);
            });
        };
    }

    private static LongDisposeTracker Instance { get; } = new LongDisposeTracker();

    private LongDisposeTracker()
    {
    }

    public PipelinePhase Phase => PipelinePhase.Activation;

    public void Execute(ResolveRequestContext context, Action<ResolveRequestContext> next)
    {
        next(context);

        if (context.Registration.Ownership == InstanceOwnership.OwnedByLifetimeScope)
        {
            // Note: In practice, the two method add to the exact same stack, so only the wrapper is important.
            if (context.Instance is IAsyncDisposable asyncDisposable)
            {
                IAsyncDisposable wrapped = context.Instance is IDisposable
                    ? new DisposableAndAsyncDisposableTracker(asyncDisposable)
                    : new AsyncDisposableTracker(asyncDisposable);
                context.ActivationScope.Disposer.AddInstanceForAsyncDisposal(wrapped);
            }
            else if (context.Instance is IDisposable disposable)
            {
                context.ActivationScope.Disposer.AddInstanceForDisposal(new DisposableTracker(disposable));
            }
        }
    }

    public override string ToString() => nameof(LongDisposeTracker);

    private class DisposableTracker(IDisposable disposable) : IDisposable
    {
        public void Dispose()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            disposable.Dispose();
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(1))
            {
                TestContext.Error.WriteLine($"{disposable} took {stopwatch.Elapsed} to dispose");
            }
        }
    }

    private class AsyncDisposableTracker(IAsyncDisposable disposable) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            await disposable.DisposeAsync();
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(1))
            {
                TestContext.Error.WriteLine($"{disposable} took {stopwatch.Elapsed} to async dispose");
            }
        }
    }

    private class DisposableAndAsyncDisposableTracker(IAsyncDisposable disposable) : IAsyncDisposable, IDisposable
    {
        public ValueTask DisposeAsync()
        {
            return new AsyncDisposableTracker(disposable).DisposeAsync();
        }

        public void Dispose()
        {
            new DisposableTracker((disposable as IDisposable)!).Dispose();
        }
    }
}
