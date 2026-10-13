// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.State.Flat.Rust.Interop;
using NUnit.Framework;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>The call leases of native read-only bundles and scopes, which keep the native object alive for each call.</summary>
/// <remarks>Not parallel: a scope disposed here must hand its stripes to the next one through the process-wide pool.</remarks>
[NonParallelizable]
public class BundleLeaseTests
{
    private static readonly TimeSpan Briefly = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Eventually = TimeSpan.FromSeconds(30);

    /// <summary>A lease reads through the handle, a scope's as its base view, and none is given once the handle is disposed.</summary>
    [Test]
    public async Task A_lease_reads_the_bundle_until_the_handle_is_disposed([Values] bool scope)
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        await using FlatManagerContainer container = new(FlatManagerContainer.Config(FlatDbBackend.Rust), path.Path, new TestFinality());
        BundleBaseHandle bundle = Open(container, scope);

        Assert.That(GetAccount(bundle), Is.EqualTo((FdbStatus.Ok, (byte)0, ReadSource.Persistence)));
        bundle.Dispose();

        Assert.That(() => { using BundleBaseHandle.Call call = bundle.Enter(); }, Throws.TypeOf<ObjectDisposedException>());
    }

    /// <summary>
    /// Disposing a scope waits for the call in flight; disposing it again leaves alone the stripes it gave back, which
    /// the next scope gets with every count back at zero.
    /// </summary>
    [Test]
    public async Task Disposing_a_scope_waits_for_its_calls_once()
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        await using FlatManagerContainer container = new(FlatManagerContainer.Config(FlatDbBackend.Rust), path.Path, new TestFinality());
        BundleBaseHandle first = Open(container, scope: true);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        Task call = Task.Run(() =>
        {
            using BundleBaseHandle.Call lease = first.Enter();
            entered.Set();
            release.Wait();
        });
        entered.Wait();

        Task disposal = Task.Run(first.Dispose);
        bool waitedForTheCall = !disposal.Wait(Briefly);
        release.Set();
        await call;
        Assert.That((waitedForTheCall, disposal.Wait(Eventually)), Is.EqualTo((true, true)));

        BundleBaseHandle second = Open(container, scope: true);
        using (second.Enter())
        {
            Assert.That(Task.Run(first.Dispose).Wait(Eventually), Is.True, "a second dispose does not wait for another scope's call");
        }

        Assert.That(GetAccount(second).Status, Is.EqualTo(FdbStatus.Ok));
        Assert.That(Task.Run(second.Dispose).Wait(Eventually), Is.True, "every stripe drained");
    }

    /// <summary>A read-only bundle, or a scope, of the empty state, holding its references on the store and the manager.</summary>
    private static unsafe BundleBaseHandle Open(FlatManagerContainer container, bool scope)
    {
        NativeFlatManager native = container.Resolve<NativeFlatManager>();
        RustFlatStore store = container.Resolve<RustPersistence>().Store;
        ManagerHandle manager = native.Handle;
        store.AddChildReference();
        bool managerAdded = false;
        try
        {
            manager.DangerousAddRef(ref managerAdded);
            FfiStateId preGenesis = new(StateId.PreGenesis);
            FfiScopeFlags flags = new(filterInMemorySlotReads: false, ResourcePool.Usage.MainBlockProcessing);
            nint bundle;
            FfiRobInfo info;
            native.ThrowIfFailed(scope
                ? NativeMethods.fdb_scope_open(manager, &preGenesis, 0, &flags, &bundle, &info)
                : NativeMethods.fdb_rob_gather(manager, &preGenesis, 0, &bundle, &info));
            return scope ? new ScopeHandle(store, manager, bundle) : new RobHandle(store, manager, bundle);
        }
        catch
        {
            if (managerAdded) manager.DangerousRelease();
            store.ReleaseChildReference();
            throw;
        }
    }

    private static unsafe (FdbStatus Status, byte Found, ReadSource Source) GetAccount(BundleBaseHandle bundle)
    {
        byte* address = stackalloc byte[Address.Size];
        FfiAccount account;
        byte found;
        ReadSource source;
        using BundleBaseHandle.Call call = bundle.Enter();
        FdbStatus status = NativeMethods.fdb_rob_get_account(call.Bundle, address, &account, &found, &source);
        return (status, found, source);
    }
}
