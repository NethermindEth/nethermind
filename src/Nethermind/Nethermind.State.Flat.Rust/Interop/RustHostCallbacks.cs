// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.State.Flat.Rust.Interop;

/// <summary>
/// The C# services the native persistence manager calls, as <c>PersistenceManager</c> injects them: the finality
/// boundary of <see cref="IStateHeaderProvider"/> and <see cref="IStatePersistenceBarrier.FlushDeferred"/>.
/// </summary>
/// <remarks>
/// The callbacks run on the native persistence thread, or on the thread flushing the manager, with the native
/// persistence lock held. An exception never crosses into native code: a failed finality lookup reads as no finality,
/// and a failed flush aborts that persist, as it does in C#.
/// </remarks>
internal sealed unsafe class RustHostCallbacks(IStateHeaderProvider stateHeaderProvider, IStatePersistenceBarrier persistenceBarrier, ILogManager logManager)
{
    private const int FlushOk = 0;
    private const int FlushFailed = 1;
    private const int FlushCancelled = (int)FdbStatus.Cancelled;

    private readonly ILogger _logger = logManager.GetClassLogger<RustHostCallbacks>();

    /// <summary>The callbacks, with <paramref name="context"/> a <see cref="GCHandle"/> of this instance.</summary>
    public static FdbHost ToNative(GCHandle context) => new()
    {
        Ctx = GCHandle.ToIntPtr(context),
        FinalizedBlockNumber = (nint)(delegate* unmanaged[Cdecl]<nint, ulong>)&FinalizedBlockNumber,
        FinalizedStateRoot = (nint)(delegate* unmanaged[Cdecl]<nint, ulong, byte*, byte>)&FinalizedStateRoot,
        FlushDeferred = (nint)(delegate* unmanaged[Cdecl]<nint, int>)&FlushDeferred,
    };

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong FinalizedBlockNumber(nint context) => Of(context).ReadFinalizedBlockNumber();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte FinalizedStateRoot(nint context, ulong blockNumber, byte* root) =>
        Of(context).ReadFinalizedStateRoot(blockNumber, new Span<byte>(root, Hash256.Size)) ? (byte)1 : (byte)0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FlushDeferred(nint context) => Of(context).RunFlushDeferred();

    private static RustHostCallbacks Of(nint context) => (RustHostCallbacks)GCHandle.FromIntPtr(context).Target!;

    private ulong ReadFinalizedBlockNumber()
    {
        try
        {
            return stateHeaderProvider.FinalizedBlockNumber;
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error("Reading the finalized block number for the flat DB failed.", exception);
            return 0;
        }
    }

    private bool ReadFinalizedStateRoot(ulong blockNumber, Span<byte> root)
    {
        try
        {
            if (stateHeaderProvider.GetFinalizedHeader(blockNumber)?.StateRoot is not { } stateRoot) return false;

            stateRoot.Bytes.CopyTo(root);
            return true;
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error($"Reading the finalized header {blockNumber} for the flat DB failed.", exception);
            return false;
        }
    }

    private int RunFlushDeferred()
    {
        try
        {
            persistenceBarrier.FlushDeferred();
            return FlushOk;
        }
        catch (OperationCanceledException)
        {
            return FlushCancelled;
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error("Block-data flush failed before persisting state; aborting this persist.", exception);
            return FlushFailed;
        }
    }
}
