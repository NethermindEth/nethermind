using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Int256;
using Sigil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Evm.CodeAnalysis.IL.CompilerModes;

public class Locals<T>(Emit<T> method) : IDisposable
{
    public Local jmpDestination = method.DeclareLocal(typeof(int));
    public Local address = method.DeclareLocal(typeof(Address));
    public Local hash256 = method.DeclareLocal(typeof(Hash256));
    public Local wordRef256A = method.DeclareLocal(typeof(Word).MakeByRefType());
    public Local wordRef256B = method.DeclareLocal(typeof(Word).MakeByRefType());
    public Local wordRef256C = method.DeclareLocal(typeof(Word).MakeByRefType());
    public Local uint256A = method.DeclareLocal(typeof(UInt256));
    public Local uint256B = method.DeclareLocal(typeof(UInt256));
    public Local uint256C = method.DeclareLocal(typeof(UInt256));
    public Local uint256R = method.DeclareLocal(typeof(UInt256));
    public Local localReadOnlyMemory = method.DeclareLocal(typeof(ReadOnlyMemory<byte>));
    public Local localReadonOnlySpan = method.DeclareLocal(typeof(ReadOnlySpan<byte>));
    public Local localZeroPaddedSpan = method.DeclareLocal(typeof(ZeroPaddedSpan));
    public Local localSpan = method.DeclareLocal(typeof(Span<byte>));
    public Local localMemory = method.DeclareLocal(typeof(Memory<byte>));
    public Local localArray = method.DeclareLocal(typeof(byte[]));
    public Local uint64A = method.DeclareLocal(typeof(ulong));
    public Local uint32A = method.DeclareLocal(typeof(uint));
    public Local uint32B = method.DeclareLocal(typeof(uint));
    public Local int64A = method.DeclareLocal(typeof(long));
    public Local int64B = method.DeclareLocal(typeof(long));
    public Local byte8A = method.DeclareLocal(typeof(byte));
    public Local lbool = method.DeclareLocal(typeof(bool));
    public Local byte8B = method.DeclareLocal(typeof(byte));
    public Local storageCell = method.DeclareLocal(typeof(StorageCell));
    public Local gasAvailable = method.DeclareLocal(typeof(long));
    public Local programCounter = method.DeclareLocal(typeof(int));
    public Local stackHeadRef = method.DeclareLocal(typeof(Word).MakeByRefType());
    public Local stackHeadIdx = method.DeclareLocal(typeof(int));
    public Local header = method.DeclareLocal(typeof(BlockHeader));

    public Dictionary<string, Local> AddtionalLocals = new();

    public bool TryDeclareLocal(string name, Type type)
    {
        if (!AddtionalLocals.ContainsKey(name))
        {
            AddtionalLocals.Add(name, method.DeclareLocal(type));
            return true;
        }
        return false;
    }

    public bool TryLoadLocal(string name, bool byAddress = false)
    {
        if (AddtionalLocals.ContainsKey(name))
        {
            if (byAddress)
                method.LoadLocalAddress(AddtionalLocals[name]);
            else
                method.LoadLocal(AddtionalLocals[name]);
            return true;
        }
        return false;
    }

    public bool TryStoreLocal(string name)
    {
        if (AddtionalLocals.ContainsKey(name))
        {
            method.StoreLocal(AddtionalLocals[name]);
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        jmpDestination.Dispose();
        address.Dispose();
        hash256.Dispose();
        wordRef256A.Dispose();
        wordRef256B.Dispose();
        wordRef256C.Dispose();
        uint256A.Dispose();
        uint256B.Dispose();
        uint256C.Dispose();
        uint256R.Dispose();
        localReadOnlyMemory.Dispose();
        localReadonOnlySpan.Dispose();
        localZeroPaddedSpan.Dispose();
        localSpan.Dispose();
        localMemory.Dispose();
        localArray.Dispose();
        uint64A.Dispose();
        uint32A.Dispose();
        uint32B.Dispose();
        int64A.Dispose();
        int64B.Dispose();
        byte8A.Dispose();
        lbool.Dispose();
        byte8B.Dispose();
        storageCell.Dispose();
        gasAvailable.Dispose();
        programCounter.Dispose();
        stackHeadRef.Dispose();
        stackHeadIdx.Dispose();
        header.Dispose();

        foreach (var local in AddtionalLocals)
        {
            local.Value.Dispose();
        }
    }

}
