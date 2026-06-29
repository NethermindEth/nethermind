// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

// Generated from proto/vm/runtime/runtime.proto (package "vm.runtime" => C# namespace "Vm.Runtime").
using RuntimePb = global::Vm.Runtime;

namespace Nethermind.Avalanche.Vm.Test;

/// <summary>
/// Stands in for AvalancheGo's <c>vm.runtime.Runtime</c> service: the endpoint a freshly launched
/// rpcchainvm plugin calls back into during the reverse-gRPC handshake. The single
/// <see cref="Initialize"/> callback records the protocol version and VM address the plugin reports.
/// </summary>
/// <remarks>
/// The captured handshake details are surfaced through <see cref="Initialized"/>, which completes the
/// first time the VM invokes <c>Initialize</c>, so the test can assert on them and then dial the VM.
/// </remarks>
public sealed class FakeRuntimeService : RuntimePb.Runtime.RuntimeBase
{
    private readonly TaskCompletionSource<RuntimePb.InitializeRequest> _initialized =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes with the request the VM sent the first time it calls <c>Initialize</c>.</summary>
    public Task<RuntimePb.InitializeRequest> Initialized => _initialized.Task;

    public override Task<Empty> Initialize(RuntimePb.InitializeRequest request, ServerCallContext context)
    {
        _initialized.TrySetResult(request);
        return Task.FromResult(new Empty());
    }
}
