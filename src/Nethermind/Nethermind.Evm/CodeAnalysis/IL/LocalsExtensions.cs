using System;
using System.Collections.Generic;
using System.Threading;
using Sigil;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.CodeAnalysis.IL;

public class Locals<T> : IDisposable
{
    private Namer namer = new();

    public Local jmpDestination;
    public Local address;
    public Local hash256;
    public Local wordRef256A;
    public Local wordRef256B;
    public Local wordRef256C;
    public Local uint256A;
    public Local uint256B;
    public Local uint256C;
    public Local uint256R;
    public Local localReadOnlyMemory;
    public Local localReadonOnlySpan;
    public Local localZeroPaddedSpan;
    public Local localSpan;
    public Local localMemory;
    public Local localArray;
    public Local uint64A;
    public Local uint32A;
    public Local uint32B;
    public Local int64A;
    public Local int64B;
    public Local byte8A;
    public Local lbool;
    public Local byte8B;
    public Local storageCell;
    public Local gasAvailable;
    public Local programCounter;
    public Local stackHeadRef;
    public Local stackHeadIdx;
    public Local header;

    public Dictionary<string, Local> AddtionalLocals = new();
    private Emit<T> _method;

    public Locals(Emit<T> method)
    {
        _method = method;
        jmpDestination = method.DeclareLocal(typeof(int), namer.GetLocalName());
        address = method.DeclareLocal(typeof(Address), namer.GetLocalName());
        hash256 = method.DeclareLocal(typeof(Hash256), namer.GetLocalName());
        wordRef256A = method.DeclareLocal(typeof(Word).MakeByRefType(), namer.GetLocalName());
        wordRef256B = method.DeclareLocal(typeof(Word).MakeByRefType(), namer.GetLocalName());
        wordRef256C = method.DeclareLocal(typeof(Word).MakeByRefType(), namer.GetLocalName());
        uint256A = method.DeclareLocal(typeof(UInt256), namer.GetLocalName());
        uint256B = method.DeclareLocal(typeof(UInt256), namer.GetLocalName());
        uint256C = method.DeclareLocal(typeof(UInt256), namer.GetLocalName());
        uint256R = method.DeclareLocal(typeof(UInt256), namer.GetLocalName());
        localReadOnlyMemory = method.DeclareLocal(typeof(ReadOnlyMemory<byte>), namer.GetLocalName());
        localReadonOnlySpan = method.DeclareLocal(typeof(ReadOnlySpan<byte>), namer.GetLocalName());
        localZeroPaddedSpan = method.DeclareLocal(typeof(ZeroPaddedSpan), namer.GetLocalName());
        localSpan = method.DeclareLocal(typeof(Span<byte>), namer.GetLocalName());
        localMemory = method.DeclareLocal(typeof(Memory<byte>), namer.GetLocalName());
        localArray = method.DeclareLocal(typeof(byte[]), namer.GetLocalName());
        uint64A = method.DeclareLocal(typeof(ulong), namer.GetLocalName());
        uint32A = method.DeclareLocal(typeof(uint), namer.GetLocalName());
        uint32B = method.DeclareLocal(typeof(uint), namer.GetLocalName());
        int64A = method.DeclareLocal(typeof(long), namer.GetLocalName());
        int64B = method.DeclareLocal(typeof(long), namer.GetLocalName());
        byte8A = method.DeclareLocal(typeof(byte), namer.GetLocalName());
        lbool = method.DeclareLocal(typeof(bool), namer.GetLocalName());
        byte8B = method.DeclareLocal(typeof(byte), namer.GetLocalName());
        storageCell = method.DeclareLocal(typeof(StorageCell), namer.GetLocalName());
        gasAvailable = method.DeclareLocal(typeof(long), namer.GetLocalName());
        programCounter = method.DeclareLocal(typeof(int), namer.GetLocalName());
        stackHeadRef = method.DeclareLocal(typeof(Word).MakeByRefType(), namer.GetLocalName());
        stackHeadIdx = method.DeclareLocal(typeof(int), namer.GetLocalName());
        header = method.DeclareLocal(typeof(BlockHeader), namer.GetLocalName());
    }

    public string GetLabelName() => namer.GetLabelName();
    public string GetLocalName() => namer.GetLocalName();

    public bool TryDeclareLocal(string name, Type type)
    {
        if (!AddtionalLocals.ContainsKey(name))
        {
            AddtionalLocals.Add(name, _method.DeclareLocal(type, namer.GetLocalName()));
            return true;
        }
        return false;
    }

    public bool TryLoadLocal(string name, bool byAddress = false)
    {
        if (AddtionalLocals.ContainsKey(name))
        {
            if (byAddress)
                _method.LoadLocalAddress(AddtionalLocals[name]);
            else
                _method.LoadLocal(AddtionalLocals[name]);
            return true;
        }
        return false;
    }

    public bool TryStoreLocal(string name)
    {
        if (AddtionalLocals.ContainsKey(name))
        {
            _method.StoreLocal(AddtionalLocals[name]);
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

        AddtionalLocals.Clear();
    }

    public struct Namer
    {
        private long _lastLabel;
        private long _lastLocal;

        public string GetLabelName() => $"j{Interlocked.Increment(ref _lastLabel):0}";
        public string GetLocalName() => $"l{Interlocked.Increment(ref _lastLocal):0}";
    }
}
