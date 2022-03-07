//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Runtime.InteropServices;
using Nethermind.Logging;

namespace Nethermind.Trie;

[StructLayout(LayoutKind.Sequential)]
public class Proof
{
    public IntPtr ptr;
    public int len;
}

public enum DatabaseScheme
{
    MemoryDb,
    RocksDb,
    RocksDbReadOnly,
    MemoryDbReadOnly
}
    
public enum CommitScheme
{
    TestCommitment,
    PrecomputeLagrange,
}

public struct RustVerkle
{
    public CommitScheme commitScheme;
    public DatabaseScheme databaseScheme;
    public IntPtr trie;
}

public interface IVerkleTrieStore :IDisposable
{
    void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo);
        
    void FinishBlockCommit(TrieType trieType, long blockNumber);

    void HackPersistOnShutdown();
        
    IVerkleReadOnlyVerkleTrieStore AsReadOnly();
    byte[]? GetValue(Span<byte> rawKey);
    byte[]? GetValue(byte[] rawKey);
    Span<byte>  GetValueSpan(Span<byte> rawKey);
    Span<byte>  GetValueSpan(byte[] rawKey);
    void SetValue(Span<byte> rawKey, Span<byte> value);
    void SetValue(byte[] rawKey, byte[] value);
    byte[] GetStateRoot();
}

public interface IVerkleReadOnlyVerkleTrieStore : IVerkleTrieStore { }

public class VerkleTrieStore: IVerkleTrieStore
{
    private readonly RustVerkle _verkleTrie;
    private readonly ILogger _logger;
    
    public VerkleTrieStore(DatabaseScheme databaseScheme, CommitScheme commitScheme, ILogManager? logManager)
    {
        _verkleTrie = RustVerkleLib.VerkleTrieNew(databaseScheme, commitScheme);
        _logger = logManager?.GetClassLogger<VerkleTrieStore>() ?? throw new ArgumentNullException(nameof(logManager));
    }

    public void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo)
    {
        
    }

    public void FinishBlockCommit(TrieType trieType, long blockNumber)
    {
        RustVerkleLib.VerkleTrieFlush(_verkleTrie);
    }

    public void HackPersistOnShutdown()
    {
        RustVerkleLib.VerkleTrieFlush(_verkleTrie);
    }

    public IVerkleReadOnlyVerkleTrieStore AsReadOnly()
    {
        return new ReadOnlyVerkleTrieStore(_verkleTrie);
    }
    
    public void Dispose()
    {
        if (_logger.IsDebug) _logger.Debug("Disposing trie");
        RustVerkleLib.VerkleTrieFlush(_verkleTrie);
    }

    public byte[]? GetValue(Span<byte> rawKey) => RustVerkleLib.VerkleTrieGet(_verkleTrie, rawKey);
    public byte[]? GetValue(byte[] rawKey) => RustVerkleLib.VerkleTrieGet(_verkleTrie, rawKey);
    public Span<byte>  GetValueSpan(Span<byte> rawKey) => RustVerkleLib.VerkleTrieGetSpan(_verkleTrie, rawKey);
    public Span<byte>  GetValueSpan(byte[] rawKey) => RustVerkleLib.VerkleTrieGetSpan(_verkleTrie, rawKey);
    public void SetValue(Span<byte> rawKey, Span<byte> value) => RustVerkleLib.VerkleTrieInsert(_verkleTrie, rawKey, value);
    public void SetValue(byte[] rawKey, byte[] value) => RustVerkleLib.VerkleTrieInsert(_verkleTrie, rawKey, value);
    public byte[] GetStateRoot() => RustVerkleLib.VerkleTrieGetStateRoot(_verkleTrie);

}

public class ReadOnlyVerkleTrieStore: IVerkleReadOnlyVerkleTrieStore
{
    private readonly RustVerkle _verkleTrie;
    
    public ReadOnlyVerkleTrieStore(DatabaseScheme databaseScheme, CommitScheme commitScheme)
    {
        _verkleTrie = RustVerkleLib.VerkleTrieNew(databaseScheme, commitScheme);
    }
    
    public ReadOnlyVerkleTrieStore(RustVerkle verkleTrie)
    {
        _verkleTrie = verkleTrie;
    }

    public void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo) { }

    public void FinishBlockCommit(TrieType trieType, long blockNumber) { }

    public void HackPersistOnShutdown() { }

    public IVerkleReadOnlyVerkleTrieStore AsReadOnly()
    {
        return new ReadOnlyVerkleTrieStore(_verkleTrie);
    }

    public void Dispose() {}
    
    public byte[]? GetValue(Span<byte> rawKey) => RustVerkleLib.VerkleTrieGet(_verkleTrie, rawKey);
    public byte[]? GetValue(byte[] rawKey) => RustVerkleLib.VerkleTrieGet(_verkleTrie, rawKey);
    public Span<byte>  GetValueSpan(Span<byte> rawKey) => RustVerkleLib.VerkleTrieGetSpan(_verkleTrie, rawKey);
    public Span<byte>  GetValueSpan(byte[] rawKey) => RustVerkleLib.VerkleTrieGetSpan(_verkleTrie, rawKey);
    public void SetValue(Span<byte> rawKey, Span<byte> value) => RustVerkleLib.VerkleTrieInsert(_verkleTrie, rawKey, value);
    public void SetValue(byte[] rawKey, byte[] value) => RustVerkleLib.VerkleTrieInsert(_verkleTrie, rawKey, value);
    public byte[] GetStateRoot() => RustVerkleLib.VerkleTrieGetStateRoot(_verkleTrie);
}

public static class RustVerkleLib {
    
    static RustVerkleLib()
    {
        LibResolver.Setup();
    }
    
    [DllImport("rust_verkle")]
    private static extern IntPtr verkle_trie_new(DatabaseScheme databaseScheme, CommitScheme commitScheme, [MarshalAs(UnmanagedType.LPUTF8Str)] string pathname);

    [DllImport("rust_verkle")]
    private static extern unsafe IntPtr verkle_trie_get(IntPtr verkleTrie, byte *  key);
    
    [DllImport("rust_verkle")]
    private static extern IntPtr get_root_hash(IntPtr verkleTrie);
    
    [DllImport("rust_verkle")]
    private static extern unsafe void verkle_trie_insert(IntPtr verkleTrie, byte * key, byte * value);
    
    [DllImport("rust_verkle")]
    private static extern unsafe IntPtr get_verkle_proof(IntPtr verkleTrie, byte * key);

    [DllImport("rust_verkle")]
    private static extern unsafe byte verify_verkle_proof(IntPtr verkleTrie, byte * verkleProof, int proofLen, byte * key, byte * value);

    [DllImport("rust_verkle")]
    private static extern unsafe void verkle_trie_insert_multiple(IntPtr verkleTrie, byte * keys, byte * values, int len);

    [DllImport("rust_verkle")]
    private static extern unsafe IntPtr get_verkle_proof_multiple(IntPtr verkleTrie, byte * keys, int len);

    [DllImport("rust_verkle")]
    private static extern unsafe byte verify_verkle_proof_multiple(IntPtr verkleTrie, byte * verkleProof, int proofLen, byte * keys, byte * values, int len);
    [DllImport("rust_verkle")]
    private static extern IntPtr verkle_trie_flush(IntPtr verkleTrie);
    [DllImport("rust_verkle")]
    private static extern IntPtr verkle_trie_clear(IntPtr verkleTrie);
    
    public static RustVerkle VerkleTrieNew(
        DatabaseScheme databaseScheme = DatabaseScheme.MemoryDb,
        CommitScheme commitScheme = CommitScheme.TestCommitment,
        string pathname = "./db/verkle_db"
    )
    {
        IntPtr trie = verkle_trie_new(databaseScheme, commitScheme, pathname);

        RustVerkle verkleTrie = new();
        verkleTrie.commitScheme = commitScheme;
        verkleTrie.databaseScheme = databaseScheme;
        verkleTrie.trie = trie;
        return verkleTrie;
    }
    
    public static unsafe void VerkleTrieInsert(RustVerkle verkleTrie, Span<byte> key, Span<byte> value)
    {
        int valueLength = value.Length;
        if (valueLength != 32)
        {
            throw new InvalidOperationException("Value length must be less than 32");
        }

        fixed (byte* pKey = &MemoryMarshal.GetReference(key))
        {
            fixed (byte* pValue = &MemoryMarshal.GetReference(value))
            {
                verkle_trie_insert(verkleTrie.trie, pKey, pValue);
            }
        }
    }
    
    public static unsafe void VerkleTrieInsert(RustVerkle verkleTrie, byte[] key, byte[] value)
    {
        int valueLength = value.Length;
        if (valueLength != 32)
        {
            throw new InvalidOperationException("Value length must be less than 32");
        }

        fixed (byte* pKey = key)
        {
            fixed (byte* pValue = value)
            {
                verkle_trie_insert(verkleTrie.trie, pKey, pValue);
            }
        }
    }

    public static unsafe byte[]? VerkleTrieGet(RustVerkle verkleTrie, Span<byte> key)
    {
        fixed (byte* p = &MemoryMarshal.GetReference(key))
        {
            IntPtr value = verkle_trie_get(verkleTrie.trie, p);
            if (value == IntPtr.Zero)
            {
                return null;
            }
            byte[] managedValue = new byte[32];
            Marshal.Copy(value, managedValue, 0, 32);
            return managedValue;
        }
        
    }
    
    public static unsafe byte[]? VerkleTrieGet(RustVerkle verkleTrie, byte[] key)
    {
        fixed (byte* p = key)
        {
            IntPtr value = verkle_trie_get(verkleTrie.trie, p);
            if (value == IntPtr.Zero)
            {
                return null;
            }
            byte[] managedValue = new byte[32];
            Marshal.Copy(value, managedValue, 0, 32);
            return managedValue;
        }
        
    }
    
    public static unsafe Span<byte> VerkleTrieGetSpan(RustVerkle verkleTrie, Span<byte> key)
    {
        fixed (byte* p = &MemoryMarshal.GetReference(key))
        {
            IntPtr value = verkle_trie_get(verkleTrie.trie, p);
            return value == IntPtr.Zero ? Span<byte>.Empty : new Span<byte>(value.ToPointer(), 32);
        }
    }
    
    public static unsafe Span<byte> VerkleTrieGetSpan(RustVerkle verkleTrie, byte[] key)
    {
        fixed (byte* p = key)
        {
            IntPtr value = verkle_trie_get(verkleTrie.trie, p);
            return value == IntPtr.Zero ? Span<byte>.Empty : new Span<byte>(value.ToPointer(), 32);
        }
    }
    
    public static byte[] VerkleTrieGetStateRoot(RustVerkle verkleTrie)
    {
        IntPtr value = get_root_hash(verkleTrie.trie);
        byte[] managedValue = new byte[32];
        Marshal.Copy(value, managedValue, 0, 32);
        return managedValue;
    }

    public static unsafe byte[] VerkleProofGet(RustVerkle verkleTrie, byte[] key)
    {
        fixed (byte* p = key)
        {
            IntPtr proofBox =  get_verkle_proof(verkleTrie.trie, p);
            Proof vp = (Proof)Marshal.PtrToStructure(proofBox, typeof(Proof));
            byte[] proofBytes = new byte[vp.len];
            Marshal.Copy(vp.ptr, proofBytes, 0, vp.len);
            return proofBytes;
        }
    }
    
    public static unsafe Span<byte> VerkleProofGetSpan(RustVerkle verkleTrie, byte[] key)
    {
        fixed (byte* p = key)
        {
            IntPtr proofBox =  get_verkle_proof(verkleTrie.trie, p);
            Proof vp = (Proof)Marshal.PtrToStructure(proofBox, typeof(Proof));
            return vp.ptr == IntPtr.Zero ? Span<byte>.Empty : new Span<byte>(vp.ptr.ToPointer(), vp.len);
        }
    }

    public static unsafe bool VerkleProofVerify(RustVerkle verkleTrie, byte[] verkleProof, int proofLen, byte[] key, byte[] value)
    {
        fixed(byte* pProof = verkleProof)
        {
            fixed(byte* pKey = key)
            {
                fixed(byte* pValue = value)
                {
                    byte verification = verify_verkle_proof(verkleTrie.trie, pProof, proofLen, pKey, pValue);
                    if (verification == 0)
                    {
                        return false;
                    }
                    return true;
                }
            }
        }
    }

    public static unsafe void VerkleTrieInsertMultiple(RustVerkle verkleTrie, byte[,] keys, byte[,] vals, int len)
    {
        fixed (byte*  pKey = keys)
        {
            fixed (byte* pValue = vals)
            {
                verkle_trie_insert_multiple(verkleTrie.trie, pKey, pValue, len);
            }
        }
    }

    public static unsafe byte[] VerkleProofGetMultiple(RustVerkle verkleTrie, byte[,] keys, int len)
    {
        fixed(byte* pKey = keys)
        {
            IntPtr proofBox = get_verkle_proof_multiple(verkleTrie.trie, pKey, len);
            Proof vp = (Proof)Marshal.PtrToStructure(proofBox, typeof(Proof));
            byte[] proofBytes = new byte[vp.len];
            Marshal.Copy(vp.ptr, proofBytes, 0, vp.len);
            return proofBytes;
        }
    }

    public static unsafe bool VerkleProofVerifyMultiple(RustVerkle verkleTrie, byte[] verkleProof, int proofLen, byte[,] keys, byte[,] values, int len)
    {
        fixed(byte* pProof = verkleProof)
        {
            fixed(byte* pKey = keys)
            {
                fixed(byte* pValue = values)
                {
                    byte verification = verify_verkle_proof_multiple(verkleTrie.trie, pProof, proofLen, pKey, pValue, len);
                    if (verification == 0)
                    {
                        return false;
                    }
                    return true;
                }
            }
        }
    }
    
    public static void VerkleTrieFlush(RustVerkle verkleTrie)
    {
        if (verkleTrie.databaseScheme == DatabaseScheme.RocksDbReadOnly)
        {
            throw new InvalidOperationException("Readonly trie cannot be flushed");
        }
        verkle_trie_flush(verkleTrie.trie);
    }
    
    public static void VerkleTrieClear(RustVerkle verkleTrie)
    {
        verkle_trie_clear(verkleTrie.trie);
    }

    public static RustVerkle VerkleTrieGetReadOnly(RustVerkle verkleTrie)
    {
        RustVerkle verkleTrieNew = new();
        verkleTrieNew.commitScheme =verkleTrie.commitScheme;
        verkleTrieNew.trie = verkleTrie.trie;
        if (verkleTrie.databaseScheme == DatabaseScheme.RocksDb)
        {
            verkleTrieNew.databaseScheme = DatabaseScheme.RocksDbReadOnly;
        }
        else if (verkleTrie.databaseScheme == DatabaseScheme.MemoryDb)
        {
            verkleTrieNew.databaseScheme = DatabaseScheme.MemoryDbReadOnly;
        }
        else
        {
            verkleTrieNew.databaseScheme = verkleTrie.databaseScheme;
        }

        return verkleTrie;
    }
}

