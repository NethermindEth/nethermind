// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

// Generated from proto/vm/vm.proto (package "vm" => C# namespace "Vm").
// Aliased to avoid clashing with this assembly's "Nethermind.Avalanche.Vm" namespace.
using VmPb = global::Vm;

namespace Nethermind.Avalanche.Vm;

/// <summary>
/// Implements the AvalancheGo <c>vm.VM</c> gRPC service (rpcchainvm protocol 45). Every RPC is stubbed
/// with a sane default so the VM completes the AvalancheGo handshake and bootstrap state machine; the
/// block-lifecycle RPCs (<c>BuildBlock</c>/<c>ParseBlock</c>/<c>BlockVerify</c>/<c>BlockAccept</c>/
/// <c>BlockReject</c>) carry explicit TODOs where Nethermind block processing must be wired in.
/// </summary>
/// <remarks>
/// State sync is reported as unsupported (<c>StateSyncEnabled.enabled = false</c>). <c>WaitForEvent</c>
/// long-polls an unbounded channel; the VM signals a pending block by writing <c>MESSAGE_BUILD_BLOCK</c>
/// into it. <c>Shutdown</c> trips <see cref="ShutdownRequested"/> so the host can stop gracefully.
/// </remarks>
public sealed class AvalancheVmService : VmPb.VM.VMBase
{
    private const string VmVersion = "nethermind-avalanche-vm/0.1.0";

    private readonly Channel<VmPb.Message> _events = Channel.CreateUnbounded<VmPb.Message>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly CancellationTokenSource _shutdownCts = new();

    // The rpcdb adapter is created on Initialize from InitializeRequest.db_server_addr.
    private RpcDatabase? _database;

    /// <summary>Triggered when AvalancheGo invokes the <c>Shutdown</c> RPC. The host waits on this to stop.</summary>
    public CancellationToken ShutdownRequested => _shutdownCts.Token;

    /// <summary>The rpcdb-backed key/value store, available once <c>Initialize</c> has been called.</summary>
    public RpcDatabase? Database => _database;

    /// <summary>Enqueues a build-block notification to be delivered to the next pending <c>WaitForEvent</c>.</summary>
    public void NotifyBuildBlock() => _events.Writer.TryWrite(VmPb.Message.BuildBlock);

    public override Task<VmPb.InitializeResponse> Initialize(VmPb.InitializeRequest request, ServerCallContext context)
    {
        // Persistence is provided by AvalancheGo over rpcdb; wire up the adapter so block processing can use it.
        if (!string.IsNullOrEmpty(request.DbServerAddr))
        {
            _database = RpcDatabase.Connect(request.DbServerAddr);
        }

        // TODO: bootstrap Nethermind (genesis from request.GenesisBytes, chain config from request.ConfigBytes,
        // data dir from request.ChainDataDir) and return the last-accepted block instead of the genesis stub.
        return Task.FromResult(new VmPb.InitializeResponse
        {
            LastAcceptedId = ByteString.Empty,
            LastAcceptedParentId = ByteString.Empty,
            Height = 0,
            Bytes = ByteString.Empty,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch),
        });
    }

    public override Task<VmPb.SetStateResponse> SetState(VmPb.SetStateRequest request, ServerCallContext context) =>
        // TODO: react to STATE_SYNCING / BOOTSTRAPPING / NORMAL_OP transitions. For now echo the genesis stub.
        Task.FromResult(new VmPb.SetStateResponse
        {
            LastAcceptedId = ByteString.Empty,
            LastAcceptedParentId = ByteString.Empty,
            Height = 0,
            Bytes = ByteString.Empty,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch),
        });

    public override async Task<Empty> Shutdown(Empty request, ServerCallContext context)
    {
        if (!_shutdownCts.IsCancellationRequested)
        {
            await _shutdownCts.CancelAsync();
        }

        _events.Writer.TryComplete();

        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null;
        }

        return new Empty();
    }

    public override Task<VmPb.CreateHandlersResponse> CreateHandlers(Empty request, ServerCallContext context) =>
        // TODO: stand up a gRPC http.HTTP server for the JSON-RPC API and return its address as a Handler.
        Task.FromResult(new VmPb.CreateHandlersResponse());

    public override Task<VmPb.NewHTTPHandlerResponse> NewHTTPHandler(Empty request, ServerCallContext context) =>
        // TODO: as above, return the address of the gRPC http server serving JSON-RPC.
        Task.FromResult(new VmPb.NewHTTPHandlerResponse());

    public override async Task<VmPb.WaitForEventResponse> WaitForEvent(Empty request, ServerCallContext context)
    {
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, _shutdownCts.Token);

        try
        {
            VmPb.Message message = await _events.Reader.ReadAsync(linked.Token);
            return new VmPb.WaitForEventResponse { Message = message };
        }
        catch (OperationCanceledException)
        {
            // Channel completed on shutdown, or the call was cancelled. Report "unspecified" so the engine
            // simply re-issues WaitForEvent (or stops, if we are shutting down).
            return new VmPb.WaitForEventResponse { Message = VmPb.Message.Unspecified };
        }
        catch (ChannelClosedException)
        {
            return new VmPb.WaitForEventResponse { Message = VmPb.Message.Unspecified };
        }
    }

    public override Task<Empty> Connected(VmPb.ConnectedRequest request, ServerCallContext context) =>
        Task.FromResult(new Empty());

    public override Task<Empty> Disconnected(VmPb.DisconnectedRequest request, ServerCallContext context) =>
        Task.FromResult(new Empty());

    public override Task<VmPb.BuildBlockResponse> BuildBlock(VmPb.BuildBlockRequest request, ServerCallContext context) =>
        // TODO: ask Nethermind to assemble a block from the mempool, persist it via rpcdb, and return its
        // id/parent_id/bytes/height/timestamp. Until implemented, fail rather than return a bogus block.
        throw new RpcException(new Status(StatusCode.Unimplemented, "BuildBlock is not implemented yet"));

    public override Task<VmPb.ParseBlockResponse> ParseBlock(VmPb.ParseBlockRequest request, ServerCallContext context) =>
        // TODO: decode request.Bytes into a Nethermind block and return its header fields.
        throw new RpcException(new Status(StatusCode.Unimplemented, "ParseBlock is not implemented yet"));

    public override Task<VmPb.GetBlockResponse> GetBlock(VmPb.GetBlockRequest request, ServerCallContext context) =>
        // TODO: look up the block by id (via rpcdb / Nethermind block store). On a miss, AvalancheGo expects
        // ERROR_NOT_FOUND in the response rather than a gRPC error.
        Task.FromResult(new VmPb.GetBlockResponse { Err = VmPb.Error.NotFound });

    public override Task<Empty> SetPreference(VmPb.SetPreferenceRequest request, ServerCallContext context) =>
        // TODO: set the preferred head used as the parent of the next BuildBlock.
        Task.FromResult(new Empty());

    public override Task<VmPb.HealthResponse> Health(Empty request, ServerCallContext context) =>
        // Empty details with an OK gRPC status is interpreted as healthy.
        Task.FromResult(new VmPb.HealthResponse { Details = ByteString.Empty });

    public override Task<VmPb.VersionResponse> Version(Empty request, ServerCallContext context) =>
        Task.FromResult(new VmPb.VersionResponse { Version = VmVersion });

    public override Task<Empty> AppRequest(VmPb.AppRequestMsg request, ServerCallContext context) =>
        Task.FromResult(new Empty());

    public override Task<Empty> AppRequestFailed(VmPb.AppRequestFailedMsg request, ServerCallContext context) =>
        Task.FromResult(new Empty());

    public override Task<Empty> AppResponse(VmPb.AppResponseMsg request, ServerCallContext context) =>
        Task.FromResult(new Empty());

    public override Task<Empty> AppGossip(VmPb.AppGossipMsg request, ServerCallContext context) =>
        Task.FromResult(new Empty());

    public override Task<VmPb.GatherResponse> Gather(Empty request, ServerCallContext context) =>
        // TODO: surface Nethermind metrics as prometheus MetricFamily entries.
        Task.FromResult(new VmPb.GatherResponse());

    public override Task<VmPb.GetAncestorsResponse> GetAncestors(VmPb.GetAncestorsRequest request, ServerCallContext context) =>
        // TODO: return up to request.MaxBlocksNum serialized ancestors of request.BlkId.
        Task.FromResult(new VmPb.GetAncestorsResponse());

    public override async Task<VmPb.BatchedParseBlockResponse> BatchedParseBlock(VmPb.BatchedParseBlockRequest request, ServerCallContext context)
    {
        VmPb.BatchedParseBlockResponse response = new();
        for (int i = 0; i < request.Request.Count; i++)
        {
            VmPb.ParseBlockResponse parsed =
                await ParseBlock(new VmPb.ParseBlockRequest { Bytes = request.Request[i] }, context);
            response.Response.Add(parsed);
        }

        return response;
    }

    public override Task<VmPb.GetBlockIDAtHeightResponse> GetBlockIDAtHeight(VmPb.GetBlockIDAtHeightRequest request, ServerCallContext context) =>
        // TODO: map height -> accepted block id. Report a miss until the height index is wired in.
        Task.FromResult(new VmPb.GetBlockIDAtHeightResponse { Err = VmPb.Error.NotFound });

    public override Task<VmPb.StateSyncEnabledResponse> StateSyncEnabled(Empty request, ServerCallContext context) =>
        Task.FromResult(new VmPb.StateSyncEnabledResponse { Enabled = false, Err = VmPb.Error.Unspecified });

    public override Task<VmPb.GetOngoingSyncStateSummaryResponse> GetOngoingSyncStateSummary(Empty request, ServerCallContext context) =>
        Task.FromResult(new VmPb.GetOngoingSyncStateSummaryResponse { Err = VmPb.Error.StateSyncNotImplemented });

    public override Task<VmPb.GetLastStateSummaryResponse> GetLastStateSummary(Empty request, ServerCallContext context) =>
        Task.FromResult(new VmPb.GetLastStateSummaryResponse { Err = VmPb.Error.StateSyncNotImplemented });

    public override Task<VmPb.ParseStateSummaryResponse> ParseStateSummary(VmPb.ParseStateSummaryRequest request, ServerCallContext context) =>
        Task.FromResult(new VmPb.ParseStateSummaryResponse { Err = VmPb.Error.StateSyncNotImplemented });

    public override Task<VmPb.GetStateSummaryResponse> GetStateSummary(VmPb.GetStateSummaryRequest request, ServerCallContext context) =>
        Task.FromResult(new VmPb.GetStateSummaryResponse { Err = VmPb.Error.StateSyncNotImplemented });

    public override Task<VmPb.BlockVerifyResponse> BlockVerify(VmPb.BlockVerifyRequest request, ServerCallContext context) =>
        // TODO: run Nethermind block validation/execution over request.Bytes and return the block timestamp.
        throw new RpcException(new Status(StatusCode.Unimplemented, "BlockVerify is not implemented yet"));

    public override Task<Empty> BlockAccept(VmPb.BlockAcceptRequest request, ServerCallContext context) =>
        // TODO: commit the block identified by request.Id (advance head, flush state to rpcdb via WriteBatch).
        throw new RpcException(new Status(StatusCode.Unimplemented, "BlockAccept is not implemented yet"));

    public override Task<Empty> BlockReject(VmPb.BlockRejectRequest request, ServerCallContext context) =>
        // TODO: drop the block identified by request.Id and any speculative state derived from it.
        throw new RpcException(new Status(StatusCode.Unimplemented, "BlockReject is not implemented yet"));

    public override Task<VmPb.StateSummaryAcceptResponse> StateSummaryAccept(VmPb.StateSummaryAcceptRequest request, ServerCallContext context) =>
        Task.FromResult(new VmPb.StateSummaryAcceptResponse
        {
            Mode = VmPb.StateSummaryAcceptResponse.Types.Mode.Skipped,
            Err = VmPb.Error.StateSyncNotImplemented,
        });
}
