// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.State.Flat.Rust.Interop;

/// <summary>Turns native status codes into exceptions carrying the message of <c>fdb_last_error</c>.</summary>
internal static unsafe class StatusMap
{
    private const int InitialMessageCapacity = 512;

    public static void ThrowIfFailed(FdbStatus status)
    {
        if (status != FdbStatus.Ok) throw ToException(status, LastError());
    }

    public static Exception ToException(FdbStatus status, string message) => status switch
    {
        FdbStatus.InvalidArgument => new ArgumentException(message),
        FdbStatus.NotSupported => new NotSupportedException(message),
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
