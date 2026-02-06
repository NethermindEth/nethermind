// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.EthProofValidator.Models;
using Nethermind.EthProofValidator.Native;

namespace Nethermind.EthProofValidator.Verifiers;

public class ZkProofVerifier : IDisposable
{
    private readonly ZKType _zkType;
    private nint _vkPtr;
    private nuint _vkLen;

    private bool _disposed;

    public ZKType ZkType => _zkType;

    public ZkProofVerifier(ZKType zkType, string? vkBinary)
    {
        _zkType = zkType;
        if (!string.IsNullOrEmpty(vkBinary)) AllocateVkMemory(vkBinary);
    }

    public ZkResult Verify(byte[] proof)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            var result = NativeMethods.verify((int)_zkType, proof, (nuint)proof.Length, _vkPtr, _vkLen);
            return result switch
            {
                1 => ZkResult.Valid,
                0 => ZkResult.Invalid,
                _ => ZkResult.Failed
            };
        }
        catch
        {
            return ZkResult.Failed;
        }
    }

    private void AllocateVkMemory(string vkBinary)
    {
        byte[] vkBytes = Convert.FromBase64String(vkBinary);
        _vkLen = (nuint)vkBytes.Length;
        // Allocate unmanaged memory and copy the verification key bytes
        _vkPtr = Marshal.AllocHGlobal(vkBytes.Length);
        try
        {
            Marshal.Copy(vkBytes, 0, _vkPtr, vkBytes.Length);
        }
        catch
        {
            Marshal.FreeHGlobal(_vkPtr);
            throw;
        }
    }

    // --- Disposal Pattern ---
    public void Dispose()
    {
        if (_disposed) return;
        ReleaseVerificationKey();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void ReleaseVerificationKey()
    {
        if (_vkPtr != nint.Zero)
        {
            Marshal.FreeHGlobal(_vkPtr);
            _vkPtr = nint.Zero;
        }
    }

    ~ZkProofVerifier() => ReleaseVerificationKey();
}
