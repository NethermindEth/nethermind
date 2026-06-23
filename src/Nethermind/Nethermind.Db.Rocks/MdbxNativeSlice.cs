// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;

namespace Nethermind.Db.Rocks;

internal static class MdbxNativeSlice
{
    public static ReadOnlySpan<byte> Pin(byte[]? data, out IntPtr handle)
    {
        if (data is null)
        {
            handle = IntPtr.Zero;
            return default;
        }

        GCHandle gcHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
        handle = GCHandle.ToIntPtr(gcHandle);
        // The returned span is valid until Release frees the pinned GCHandle.
        unsafe
        {
            return new ReadOnlySpan<byte>((void*)gcHandle.AddrOfPinnedObject(), data.Length);
        }
    }

    public static void Release(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            GCHandle.FromIntPtr(handle).Free();
        }
    }
}
