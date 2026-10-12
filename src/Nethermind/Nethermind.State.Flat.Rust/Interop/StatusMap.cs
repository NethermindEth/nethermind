// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.ExceptionServices;
using System.Text;
using Nethermind.Core.Exceptions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Flat.Rust.Interop;

/// <summary>Turns native status codes into the exceptions the C# persistence throws, with the message of <c>fdb_last_error</c>.</summary>
internal static unsafe class StatusMap
{
    private const int InitialMessageCapacity = 512;

    public static void ThrowIfFailed(FdbStatus status)
    {
        if (status != FdbStatus.Ok) Throw(status);
    }

    /// <summary>Throws the exception of a failed call: a managed callback's own exception, or one built from the status.</summary>
    public static void Throw(FdbStatus status)
    {
        if (status == FdbStatus.ManagedCallback && ManagedKvBridge.TakeCallbackException() is { } callbackException)
        {
            ExceptionDispatchInfo.Throw(callbackException);
        }

        throw ToException(status, LastError());
    }

    public static Exception ToException(FdbStatus status, string message) => status switch
    {
        FdbStatus.InvalidArgument => new ArgumentException(message),
        FdbStatus.NotSupported => new NotSupportedException(message),
        FdbStatus.InvalidOperation or FdbStatus.WrongBaseState => new InvalidOperationException(message),
        FdbStatus.Rlp => new RlpException(message),
        FdbStatus.InvalidConfiguration => new InvalidConfigurationException(message, -1),
        FdbStatus.Cancelled => new OperationCanceledException(message),
        FdbStatus.ShutDown => new ObjectDisposedException(null, message),
        _ => new RustFlatDbException(status, message),
    };

    /// <summary>The message of the last failed native call on this thread.</summary>
    public static string LastError()
    {
        byte[] buffer = new byte[InitialMessageCapacity];
        while (true)
        {
            nuint length;
            fixed (byte* data = buffer)
            {
                length = NativeMethods.fdb_last_error(data, (nuint)buffer.Length);
            }

            if (length <= (nuint)buffer.Length) return Encoding.UTF8.GetString(buffer, 0, (int)length);
            buffer = new byte[(int)length];
        }
    }
}

/// <summary>A failure reported by the native flat DB library.</summary>
internal sealed class RustFlatDbException(FdbStatus status, string message) : Exception(message)
{
    public FdbStatus Status => status;
}
