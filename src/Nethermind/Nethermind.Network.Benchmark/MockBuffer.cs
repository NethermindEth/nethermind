// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Common;
using DotNetty.Common.Utilities;

namespace Nethermind.Network.Benchmarks
{
    public class MockBuffer : IByteBuffer
    {
        public IReferenceCounted Retain()
        {
            throw new NotImplementedException();
        }

        public IReferenceCounted Retain(int increment)
        {
            throw new NotImplementedException();
        }

        public IReferenceCounted Touch()
        {
            throw new NotImplementedException();
        }

        public IReferenceCounted Touch(object hint)
        {
            throw new NotImplementedException();
        }

        public bool Release()
        {
            throw new NotImplementedException();
        }

        public bool Release(int decrement)
        {
            throw new NotImplementedException();
        }

        public int ReferenceCount { get; }

        public int CompareTo(IByteBuffer other)
        {
            throw new NotImplementedException();
        }

        public bool Equals(IByteBuffer other)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer AdjustCapacity(int newCapacity)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer SetWriterIndex(int writerIndex)
        {
            return this;
        }

        public IByteBuffer SetReaderIndex(int readerIndex)
        {
            return this;
        }

        public IByteBuffer SetIndex(int readerIndex, int writerIndex)
        {
            return this;
        }

        public bool IsReadable()
        {
            return false;
        }

        public bool IsReadable(int size)
        {
            return false;
        }

        public bool IsWritable()
        {
            return true;
        }

        public bool IsWritable(int size)
        {
            return true;
        }

        public IByteBuffer Clear()
        {
            throw new NotImplementedException();
        }

        public IByteBuffer MarkReaderIndex()
        {
            throw new NotImplementedException();
        }

        public IByteBuffer ResetReaderIndex()
        {
            throw new NotImplementedException();
        }

        public IByteBuffer MarkWriterIndex()
        {
            throw new NotImplementedException();
        }

        public IByteBuffer ResetWriterIndex()
        {
            throw new NotImplementedException();
        }

        public IByteBuffer DiscardReadBytes()
        {
            return this;
        }

        public IByteBuffer DiscardSomeReadBytes()
        {
            throw new NotImplementedException();
        }

        public IByteBuffer EnsureWritable(int minWritableBytes)
        {
            throw new NotImplementedException();
        }

        public int EnsureWritable(int minWritableBytes, bool force)
        {
            return 0;
        }

        public bool GetBoolean(int index)
        {
            throw new NotImplementedException();
        }

        public byte GetByte(int index)
        {
            throw new NotImplementedException();
        }

        public short GetShort(int index)
        {
            throw new NotImplementedException();
        }

        public short GetShortLE(int index)
        {
            throw new NotImplementedException();
        }

        public ushort GetUnsignedShort(int index)
        {
            throw new NotImplementedException();
        }

        public ushort GetUnsignedShortLE(int index)
        {
            throw new NotImplementedException();
        }

        public int GetInt(int index)
        {
            throw new NotImplementedException();
        }

        public int GetIntLE(int index)
        {
            throw new NotImplementedException();
        }

        public uint GetUnsignedInt(int index)
        {
            throw new NotImplementedException();
        }

        public uint GetUnsignedIntLE(int index)
        {
            throw new NotImplementedException();
        }

        public long GetLong(int index)
        {
            throw new NotImplementedException();
        }

        public long GetLongLE(int index)
        {
            throw new NotImplementedException();
        }

        public int GetMedium(int index)
        {
            throw new NotImplementedException();
        }

        public int GetMediumLE(int index)
        {
            throw new NotImplementedException();
        }

        public int GetUnsignedMedium(int index)
        {
            throw new NotImplementedException();
        }

        public int GetUnsignedMediumLE(int index)
        {
            throw new NotImplementedException();
        }

        public char GetChar(int index)
        {
            throw new NotImplementedException();
        }

        public float GetFloat(int index)
        {
            throw new NotImplementedException();
        }

        public float GetFloatLE(int index)
        {
            throw new NotImplementedException();
        }

        public double GetDouble(int index)
        {
            throw new NotImplementedException();
        }

        public double GetDoubleLE(int index)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer GetBytes(int index, IByteBuffer destination)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer GetBytes(int index, IByteBuffer destination, int length)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer GetBytes(int index, IByteBuffer destination, int dstIndex, int length)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer GetBytes(int index, byte[] destination)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer GetBytes(int index, byte[] destination, int dstIndex, int length)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer GetBytes(int index, Stream destination, int length)
        {
            throw new NotImplementedException();
        }

        public ICharSequence GetCharSequence(int index, int length, Encoding encoding)
        {
            throw new NotImplementedException();
        }

        public string GetString(int index, int length, Encoding encoding)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer SetBoolean(int index, bool value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer SetByte(int index, int value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer SetShort(int index, int value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer SetShortLE(int index, int value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer SetUnsignedShort(int index, ushort value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer SetUnsignedShortLE(int index, ushort value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer SetInt(int index, int value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer SetIntLE(int index, int value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer SetUnsignedInt(int index, uint value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer SetUnsignedIntLE(int index, uint value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer SetMedium(int index, int value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer SetMediumLE(int index, int value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer SetLong(int index, long value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer SetLongLE(int index, long value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer SetChar(int index, char value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer SetDouble(int index, double value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer SetFloat(int index, float value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer SetDoubleLE(int index, double value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer SetFloatLE(int index, float value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer SetBytes(int index, IByteBuffer src)
        {
            return this;
        }

        public IByteBuffer SetBytes(int index, IByteBuffer src, int length)
        {
            return this;
        }

        public IByteBuffer SetBytes(int index, IByteBuffer src, int srcIndex, int length)
        {
            return this;
        }

        public IByteBuffer SetBytes(int index, byte[] src)
        {
            return this;
        }

        public IByteBuffer SetBytes(int index, byte[] src, int srcIndex, int length)
        {
            return this;
        }

        public Task<int> SetBytesAsync(int index, Stream src, int length, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer SetZero(int index, int length)
        {
            throw new NotImplementedException();
        }

        public int SetCharSequence(int index, ICharSequence sequence, Encoding encoding)
        {
            throw new NotImplementedException();
        }

        public int SetString(int index, string value, Encoding encoding)
        {
            throw new NotImplementedException();
        }

        public bool ReadBoolean()
        {
            throw new NotImplementedException();
        }

        public byte ReadByte()
        {
            throw new NotImplementedException();
        }

        public short ReadShort()
        {
            throw new NotImplementedException();
        }

        public short ReadShortLE()
        {
            throw new NotImplementedException();
        }

        public int ReadMedium()
        {
            throw new NotImplementedException();
        }

        public int ReadMediumLE()
        {
            throw new NotImplementedException();
        }

        public int ReadUnsignedMedium()
        {
            throw new NotImplementedException();
        }

        public int ReadUnsignedMediumLE()
        {
            throw new NotImplementedException();
        }

        public ushort ReadUnsignedShort()
        {
            throw new NotImplementedException();
        }

        public ushort ReadUnsignedShortLE()
        {
            throw new NotImplementedException();
        }

        public int ReadInt()
        {
            throw new NotImplementedException();
        }

        public int ReadIntLE()
        {
            throw new NotImplementedException();
        }

        public uint ReadUnsignedInt()
        {
            throw new NotImplementedException();
        }

        public uint ReadUnsignedIntLE()
        {
            throw new NotImplementedException();
        }

        public long ReadLong()
        {
            throw new NotImplementedException();
        }

        public long ReadLongLE()
        {
            throw new NotImplementedException();
        }

        public char ReadChar()
        {
            throw new NotImplementedException();
        }

        public double ReadDouble()
        {
            throw new NotImplementedException();
        }

        public double ReadDoubleLE()
        {
            throw new NotImplementedException();
        }

        public float ReadFloat()
        {
            throw new NotImplementedException();
        }

        public float ReadFloatLE()
        {
            throw new NotImplementedException();
        }

        public IByteBuffer ReadBytes(int length)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer ReadBytes(IByteBuffer destination)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer ReadBytes(IByteBuffer destination, int length)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer ReadBytes(IByteBuffer destination, int dstIndex, int length)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer ReadBytes(byte[] destination)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer ReadBytes(byte[] destination, int dstIndex, int length)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer ReadBytes(Stream destination, int length)
        {
            throw new NotImplementedException();
        }

        public ICharSequence ReadCharSequence(int length, Encoding encoding)
        {
            throw new NotImplementedException();
        }

        public string ReadString(int length, Encoding encoding)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer SkipBytes(int length)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer WriteBoolean(bool value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer WriteByte(int value)
        {
            return this;
        }

        public IByteBuffer WriteShort(int value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer WriteShortLE(int value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer WriteUnsignedShort(ushort value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer WriteUnsignedShortLE(ushort value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer WriteMedium(int value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer WriteMediumLE(int value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer WriteInt(int value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer WriteIntLE(int value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer WriteLong(long value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer WriteLongLE(long value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer WriteChar(char value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer WriteDouble(double value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer WriteDoubleLE(double value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer WriteFloat(float value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer WriteFloatLE(float value)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer WriteBytes(IByteBuffer src)
        {
            return this;
        }

        public IByteBuffer WriteBytes(IByteBuffer src, int length)
        {
            return this;
        }

        public IByteBuffer WriteBytes(IByteBuffer src, int srcIndex, int length)
        {
            return this;
        }

        public IByteBuffer WriteBytes(byte[] src)
        {
            return this;
        }

        public IByteBuffer WriteBytes(byte[] src, int srcIndex, int length)
        {
            return this;
        }

        public ArraySegment<byte> GetIoBuffer()
        {
            throw new NotImplementedException();
        }

        public ArraySegment<byte> GetIoBuffer(int index, int length)
        {
            throw new NotImplementedException();
        }

        public ArraySegment<byte>[] GetIoBuffers()
        {
            throw new NotImplementedException();
        }

        public ArraySegment<byte>[] GetIoBuffers(int index, int length)
        {
            throw new NotImplementedException();
        }

        public ref byte GetPinnableMemoryAddress()
        {
            throw new NotImplementedException();
        }

        public IntPtr AddressOfPinnedMemory()
        {
            throw new NotImplementedException();
        }

        public IByteBuffer Duplicate()
        {
            throw new NotImplementedException();
        }

        public IByteBuffer RetainedDuplicate()
        {
            throw new NotImplementedException();
        }

        public IByteBuffer Unwrap()
        {
            throw new NotImplementedException();
        }

        public IByteBuffer Copy()
        {
            throw new NotImplementedException();
        }

        public IByteBuffer Copy(int index, int length)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer Slice()
        {
            throw new NotImplementedException();
        }

        public IByteBuffer RetainedSlice()
        {
            throw new NotImplementedException();
        }

        public IByteBuffer Slice(int index, int length)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer RetainedSlice(int index, int length)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer ReadSlice(int length)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer ReadRetainedSlice(int length)
        {
            throw new NotImplementedException();
        }

        public Task WriteBytesAsync(Stream stream, int length)
        {
            throw new NotImplementedException();
        }

        public Task WriteBytesAsync(Stream stream, int length, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public IByteBuffer WriteZero(int length)
        {
            return this;
        }

        public int WriteCharSequence(ICharSequence sequence, Encoding encoding)
        {
            throw new NotImplementedException();
        }

        public int WriteString(string value, Encoding encoding)
        {
            throw new NotImplementedException();
        }

        public int IndexOf(int fromIndex, int toIndex, byte value)
        {
            throw new NotImplementedException();
        }

        public int BytesBefore(byte value)
        {
            throw new NotImplementedException();
        }

        public int BytesBefore(int length, byte value)
        {
            throw new NotImplementedException();
        }

        public int BytesBefore(int index, int length, byte value)
        {
            throw new NotImplementedException();
        }

        public string ToString(Encoding encoding)
        {
            throw new NotImplementedException();
        }

        public string ToString(int index, int length, Encoding encoding)
        {
            throw new NotImplementedException();
        }

        public int ForEachByte(IByteProcessor processor)
        {
            throw new NotImplementedException();
        }

        public int ForEachByte(int index, int length, IByteProcessor processor)
        {
            throw new NotImplementedException();
        }

        public int ForEachByteDesc(IByteProcessor processor)
        {
            throw new NotImplementedException();
        }

        public int ForEachByteDesc(int index, int length, IByteProcessor processor)
        {
            throw new NotImplementedException();
        }

        public int Capacity { get; } = int.MaxValue;
        public int MaxCapacity { get; } = int.MaxValue;
        public IByteBufferAllocator Allocator { get; }
        public bool IsDirect { get; }
        public int ReaderIndex { get; }
        public int WriterIndex { get; }
        public int ReadableBytes { get; }
        public int WritableBytes { get; } = int.MaxValue;
        public int MaxWritableBytes { get; } = int.MaxValue;
        public int IoBufferCount { get; }
        public bool HasArray { get; }
        public byte[] Array { get; } = new byte[1024 * 1024];
        public bool HasMemoryAddress { get; }
        public int ArrayOffset { get; }
    }
}
