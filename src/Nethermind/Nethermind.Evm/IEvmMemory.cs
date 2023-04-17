// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    public interface IEvmMemory : IDisposable
    {
        ulong Size { get; }
        void SaveWord(in UInt256 location, Span<byte> word);
        void SaveByte(in UInt256 location, byte value);
        void Save(in UInt256 location, Span<byte> value);
        void Save(in UInt256 location, byte[] value);
        Span<byte> LoadSpan(in UInt256 location);
        Span<byte> LoadSpan(in UInt256 location, in UInt256 length);
        ReadOnlyMemory<byte> Load(in UInt256 location, in UInt256 length);
        long CalculateMemoryCost(in UInt256 location, in UInt256 length);
        List<string> GetTrace();
    }

    public class StackableEvmMemory : IEvmMemory
    {
        private StackableEvmMemory _parent;

        private EvmPooledMemory _pooled;

        private ulong _offset;

        public StackableEvmMemory()
        {
            _pooled = new EvmPooledMemory();
        }

        public StackableEvmMemory(StackableEvmMemory stackableEvmMemory, ulong offset)
        {
            _pooled = stackableEvmMemory._pooled;
            _offset = stackableEvmMemory._offset + offset;
            _parent = stackableEvmMemory;
        }

        // need to add a method to shrink the EvmPooled - not too difficult

        public void Dispose()
        {
            if (_parent is null)
            {
                _pooled.Dispose();
            }
        }

        public ulong Size => _pooled.Size - _offset / 32;

        public void SaveWord(in UInt256 location, Span<byte> word)
        {
            _pooled.SaveWord(location + _offset, word);
        }

        public void SaveByte(in UInt256 location, byte value)
        {
            // obvious
            throw new NotImplementedException();
        }

        public void Save(in UInt256 location, Span<byte> value)
        {
            // obvious
            throw new NotImplementedException();
        }

        public void Save(in UInt256 location, byte[] value)
        {
            // obvious
            throw new NotImplementedException();
        }

        public Span<byte> LoadSpan(in UInt256 location)
        {
            // obvious
            throw new NotImplementedException();
        }

        public Span<byte> LoadSpan(in UInt256 location, in UInt256 length)
        {
            // obvious
            throw new NotImplementedException();
        }

        public ReadOnlyMemory<byte> Load(in UInt256 location, in UInt256 length)
        {
            // obvious
            throw new NotImplementedException();
        }

        public long CalculateMemoryCost(in UInt256 location, in UInt256 length)
        {
            // need to rewrite, minor challenge
            throw new NotImplementedException();
        }

        public List<string> GetTrace()
        {
            // need to rewrite but simple
            throw new NotImplementedException();
        }
    }
}
