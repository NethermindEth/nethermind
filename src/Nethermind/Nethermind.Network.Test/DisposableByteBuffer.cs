// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Common;
using DotNetty.Common.Utilities;

namespace Nethermind.Network.Test;

public sealed class DisposableByteBuffer(IByteBuffer inner) : IByteBuffer, IDisposable
{
    public void Dispose() => inner.Release();

    // IReferenceCounted
    public int ReferenceCount => inner.ReferenceCount;
    public IReferenceCounted Retain() => inner.Retain();
    public IReferenceCounted Retain(int increment) => inner.Retain(increment);
    public IReferenceCounted Touch() => inner.Touch();
    public IReferenceCounted Touch(object hint) => inner.Touch(hint);
    public bool Release() => inner.Release();
    public bool Release(int decrement) => inner.Release(decrement);

    // IComparable<IByteBuffer>, IEquatable<IByteBuffer>
    public int CompareTo(IByteBuffer other) => inner.CompareTo(other);
    public bool Equals(IByteBuffer other) => inner.Equals(other);

    // Properties
    public int Capacity => inner.Capacity;
    public int MaxCapacity => inner.MaxCapacity;
    public IByteBufferAllocator Allocator => inner.Allocator;
    public bool IsDirect => inner.IsDirect;
    public int ReaderIndex => inner.ReaderIndex;
    public int WriterIndex => inner.WriterIndex;
    public int ReadableBytes => inner.ReadableBytes;
    public int WritableBytes => inner.WritableBytes;
    public int MaxWritableBytes => inner.MaxWritableBytes;
    public int IoBufferCount => inner.IoBufferCount;
    public bool HasArray => inner.HasArray;
    public byte[] Array => inner.Array;
    public bool HasMemoryAddress => inner.HasMemoryAddress;
    public int ArrayOffset => inner.ArrayOffset;

    // Index/capacity operations
    public IByteBuffer AdjustCapacity(int newCapacity) => inner.AdjustCapacity(newCapacity);
    public IByteBuffer SetWriterIndex(int writerIndex) { inner.SetWriterIndex(writerIndex); return this; }
    public IByteBuffer SetReaderIndex(int readerIndex) { inner.SetReaderIndex(readerIndex); return this; }
    public IByteBuffer SetIndex(int readerIndex, int writerIndex) { inner.SetIndex(readerIndex, writerIndex); return this; }
    public bool IsReadable() => inner.IsReadable();
    public bool IsReadable(int size) => inner.IsReadable(size);
    public bool IsWritable() => inner.IsWritable();
    public bool IsWritable(int size) => inner.IsWritable(size);
    public IByteBuffer Clear() { inner.Clear(); return this; }
    public IByteBuffer MarkReaderIndex() { inner.MarkReaderIndex(); return this; }
    public IByteBuffer ResetReaderIndex() { inner.ResetReaderIndex(); return this; }
    public IByteBuffer MarkWriterIndex() { inner.MarkWriterIndex(); return this; }
    public IByteBuffer ResetWriterIndex() { inner.ResetWriterIndex(); return this; }
    public IByteBuffer DiscardReadBytes() { inner.DiscardReadBytes(); return this; }
    public IByteBuffer DiscardSomeReadBytes() { inner.DiscardSomeReadBytes(); return this; }
    public IByteBuffer EnsureWritable(int minWritableBytes) { inner.EnsureWritable(minWritableBytes); return this; }
    public int EnsureWritable(int minWritableBytes, bool force) => inner.EnsureWritable(minWritableBytes, force);

    // Get (non-mutating reads at absolute index)
    public bool GetBoolean(int index) => inner.GetBoolean(index);
    public byte GetByte(int index) => inner.GetByte(index);
    public short GetShort(int index) => inner.GetShort(index);
    public short GetShortLE(int index) => inner.GetShortLE(index);
    public ushort GetUnsignedShort(int index) => inner.GetUnsignedShort(index);
    public ushort GetUnsignedShortLE(int index) => inner.GetUnsignedShortLE(index);
    public int GetMedium(int index) => inner.GetMedium(index);
    public int GetMediumLE(int index) => inner.GetMediumLE(index);
    public int GetUnsignedMedium(int index) => inner.GetUnsignedMedium(index);
    public int GetUnsignedMediumLE(int index) => inner.GetUnsignedMediumLE(index);
    public int GetInt(int index) => inner.GetInt(index);
    public int GetIntLE(int index) => inner.GetIntLE(index);
    public uint GetUnsignedInt(int index) => inner.GetUnsignedInt(index);
    public uint GetUnsignedIntLE(int index) => inner.GetUnsignedIntLE(index);
    public long GetLong(int index) => inner.GetLong(index);
    public long GetLongLE(int index) => inner.GetLongLE(index);
    public char GetChar(int index) => inner.GetChar(index);
    public float GetFloat(int index) => inner.GetFloat(index);
    public float GetFloatLE(int index) => inner.GetFloatLE(index);
    public double GetDouble(int index) => inner.GetDouble(index);
    public double GetDoubleLE(int index) => inner.GetDoubleLE(index);
    public IByteBuffer GetBytes(int index, IByteBuffer destination) { inner.GetBytes(index, destination); return this; }
    public IByteBuffer GetBytes(int index, IByteBuffer destination, int length) { inner.GetBytes(index, destination, length); return this; }
    public IByteBuffer GetBytes(int index, IByteBuffer destination, int dstIndex, int length) { inner.GetBytes(index, destination, dstIndex, length); return this; }
    public IByteBuffer GetBytes(int index, byte[] destination) { inner.GetBytes(index, destination); return this; }
    public IByteBuffer GetBytes(int index, byte[] destination, int dstIndex, int length) { inner.GetBytes(index, destination, dstIndex, length); return this; }
    public IByteBuffer GetBytes(int index, Stream destination, int length) { inner.GetBytes(index, destination, length); return this; }
    public ICharSequence GetCharSequence(int index, int length, Encoding encoding) => inner.GetCharSequence(index, length, encoding);
    public string GetString(int index, int length, Encoding encoding) => inner.GetString(index, length, encoding);

    // Set (writes at absolute index)
    public IByteBuffer SetBoolean(int index, bool value) { inner.SetBoolean(index, value); return this; }
    public IByteBuffer SetByte(int index, int value) { inner.SetByte(index, value); return this; }
    public IByteBuffer SetShort(int index, int value) { inner.SetShort(index, value); return this; }
    public IByteBuffer SetShortLE(int index, int value) { inner.SetShortLE(index, value); return this; }
    public IByteBuffer SetUnsignedShort(int index, ushort value) { inner.SetUnsignedShort(index, value); return this; }
    public IByteBuffer SetUnsignedShortLE(int index, ushort value) { inner.SetUnsignedShortLE(index, value); return this; }
    public IByteBuffer SetMedium(int index, int value) { inner.SetMedium(index, value); return this; }
    public IByteBuffer SetMediumLE(int index, int value) { inner.SetMediumLE(index, value); return this; }
    public IByteBuffer SetInt(int index, int value) { inner.SetInt(index, value); return this; }
    public IByteBuffer SetIntLE(int index, int value) { inner.SetIntLE(index, value); return this; }
    public IByteBuffer SetUnsignedInt(int index, uint value) { inner.SetUnsignedInt(index, value); return this; }
    public IByteBuffer SetUnsignedIntLE(int index, uint value) { inner.SetUnsignedIntLE(index, value); return this; }
    public IByteBuffer SetLong(int index, long value) { inner.SetLong(index, value); return this; }
    public IByteBuffer SetLongLE(int index, long value) { inner.SetLongLE(index, value); return this; }
    public IByteBuffer SetChar(int index, char value) { inner.SetChar(index, value); return this; }
    public IByteBuffer SetFloat(int index, float value) { inner.SetFloat(index, value); return this; }
    public IByteBuffer SetFloatLE(int index, float value) { inner.SetFloatLE(index, value); return this; }
    public IByteBuffer SetDouble(int index, double value) { inner.SetDouble(index, value); return this; }
    public IByteBuffer SetDoubleLE(int index, double value) { inner.SetDoubleLE(index, value); return this; }
    public IByteBuffer SetBytes(int index, IByteBuffer src) { inner.SetBytes(index, src); return this; }
    public IByteBuffer SetBytes(int index, IByteBuffer src, int length) { inner.SetBytes(index, src, length); return this; }
    public IByteBuffer SetBytes(int index, IByteBuffer src, int srcIndex, int length) { inner.SetBytes(index, src, srcIndex, length); return this; }
    public IByteBuffer SetBytes(int index, byte[] src) { inner.SetBytes(index, src); return this; }
    public IByteBuffer SetBytes(int index, byte[] src, int srcIndex, int length) { inner.SetBytes(index, src, srcIndex, length); return this; }
    public Task<int> SetBytesAsync(int index, Stream src, int length, CancellationToken cancellationToken) => inner.SetBytesAsync(index, src, length, cancellationToken);
    public IByteBuffer SetZero(int index, int length) { inner.SetZero(index, length); return this; }
    public int SetCharSequence(int index, ICharSequence sequence, Encoding encoding) => inner.SetCharSequence(index, sequence, encoding);
    public int SetString(int index, string value, Encoding encoding) => inner.SetString(index, value, encoding);

    // Read (sequential reads, advance readerIndex)
    public bool ReadBoolean() => inner.ReadBoolean();
    public byte ReadByte() => inner.ReadByte();
    public short ReadShort() => inner.ReadShort();
    public short ReadShortLE() => inner.ReadShortLE();
    public ushort ReadUnsignedShort() => inner.ReadUnsignedShort();
    public ushort ReadUnsignedShortLE() => inner.ReadUnsignedShortLE();
    public int ReadMedium() => inner.ReadMedium();
    public int ReadMediumLE() => inner.ReadMediumLE();
    public int ReadUnsignedMedium() => inner.ReadUnsignedMedium();
    public int ReadUnsignedMediumLE() => inner.ReadUnsignedMediumLE();
    public int ReadInt() => inner.ReadInt();
    public int ReadIntLE() => inner.ReadIntLE();
    public uint ReadUnsignedInt() => inner.ReadUnsignedInt();
    public uint ReadUnsignedIntLE() => inner.ReadUnsignedIntLE();
    public long ReadLong() => inner.ReadLong();
    public long ReadLongLE() => inner.ReadLongLE();
    public char ReadChar() => inner.ReadChar();
    public float ReadFloat() => inner.ReadFloat();
    public float ReadFloatLE() => inner.ReadFloatLE();
    public double ReadDouble() => inner.ReadDouble();
    public double ReadDoubleLE() => inner.ReadDoubleLE();
    public IByteBuffer ReadBytes(int length) => inner.ReadBytes(length);
    public IByteBuffer ReadBytes(IByteBuffer destination) { inner.ReadBytes(destination); return this; }
    public IByteBuffer ReadBytes(IByteBuffer destination, int length) { inner.ReadBytes(destination, length); return this; }
    public IByteBuffer ReadBytes(IByteBuffer destination, int dstIndex, int length) { inner.ReadBytes(destination, dstIndex, length); return this; }
    public IByteBuffer ReadBytes(byte[] destination) { inner.ReadBytes(destination); return this; }
    public IByteBuffer ReadBytes(byte[] destination, int dstIndex, int length) { inner.ReadBytes(destination, dstIndex, length); return this; }
    public IByteBuffer ReadBytes(Stream destination, int length) { inner.ReadBytes(destination, length); return this; }
    public ICharSequence ReadCharSequence(int length, Encoding encoding) => inner.ReadCharSequence(length, encoding);
    public string ReadString(int length, Encoding encoding) => inner.ReadString(length, encoding);
    public IByteBuffer SkipBytes(int length) { inner.SkipBytes(length); return this; }
    public IByteBuffer ReadSlice(int length) => inner.ReadSlice(length);
    public IByteBuffer ReadRetainedSlice(int length) => inner.ReadRetainedSlice(length);

    // Write (sequential writes, advance writerIndex)
    public IByteBuffer WriteBoolean(bool value) { inner.WriteBoolean(value); return this; }
    public IByteBuffer WriteByte(int value) { inner.WriteByte(value); return this; }
    public IByteBuffer WriteShort(int value) { inner.WriteShort(value); return this; }
    public IByteBuffer WriteShortLE(int value) { inner.WriteShortLE(value); return this; }
    public IByteBuffer WriteUnsignedShort(ushort value) { inner.WriteUnsignedShort(value); return this; }
    public IByteBuffer WriteUnsignedShortLE(ushort value) { inner.WriteUnsignedShortLE(value); return this; }
    public IByteBuffer WriteMedium(int value) { inner.WriteMedium(value); return this; }
    public IByteBuffer WriteMediumLE(int value) { inner.WriteMediumLE(value); return this; }
    public IByteBuffer WriteInt(int value) { inner.WriteInt(value); return this; }
    public IByteBuffer WriteIntLE(int value) { inner.WriteIntLE(value); return this; }
    public IByteBuffer WriteLong(long value) { inner.WriteLong(value); return this; }
    public IByteBuffer WriteLongLE(long value) { inner.WriteLongLE(value); return this; }
    public IByteBuffer WriteChar(char value) { inner.WriteChar(value); return this; }
    public IByteBuffer WriteFloat(float value) { inner.WriteFloat(value); return this; }
    public IByteBuffer WriteFloatLE(float value) { inner.WriteFloatLE(value); return this; }
    public IByteBuffer WriteDouble(double value) { inner.WriteDouble(value); return this; }
    public IByteBuffer WriteDoubleLE(double value) { inner.WriteDoubleLE(value); return this; }
    public IByteBuffer WriteBytes(IByteBuffer src) { inner.WriteBytes(src); return this; }
    public IByteBuffer WriteBytes(IByteBuffer src, int length) { inner.WriteBytes(src, length); return this; }
    public IByteBuffer WriteBytes(IByteBuffer src, int srcIndex, int length) { inner.WriteBytes(src, srcIndex, length); return this; }
    public IByteBuffer WriteBytes(byte[] src) { inner.WriteBytes(src); return this; }
    public IByteBuffer WriteBytes(byte[] src, int srcIndex, int length) { inner.WriteBytes(src, srcIndex, length); return this; }
    public Task WriteBytesAsync(Stream stream, int length) => inner.WriteBytesAsync(stream, length);
    public Task WriteBytesAsync(Stream stream, int length, CancellationToken cancellationToken) => inner.WriteBytesAsync(stream, length, cancellationToken);
    public IByteBuffer WriteZero(int length) { inner.WriteZero(length); return this; }
    public int WriteCharSequence(ICharSequence sequence, Encoding encoding) => inner.WriteCharSequence(sequence, encoding);
    public int WriteString(string value, Encoding encoding) => inner.WriteString(value, encoding);

    // Buffer views / copies
    public IByteBuffer Duplicate() => inner.Duplicate();
    public IByteBuffer RetainedDuplicate() => inner.RetainedDuplicate();
    public IByteBuffer Unwrap() => inner.Unwrap();
    public IByteBuffer Copy() => inner.Copy();
    public IByteBuffer Copy(int index, int length) => inner.Copy(index, length);
    public IByteBuffer Slice() => inner.Slice();
    public IByteBuffer RetainedSlice() => inner.RetainedSlice();
    public IByteBuffer Slice(int index, int length) => inner.Slice(index, length);
    public IByteBuffer RetainedSlice(int index, int length) => inner.RetainedSlice(index, length);

    // IO / Memory
    public ArraySegment<byte> GetIoBuffer() => inner.GetIoBuffer();
    public ArraySegment<byte> GetIoBuffer(int index, int length) => inner.GetIoBuffer(index, length);
    public ArraySegment<byte>[] GetIoBuffers() => inner.GetIoBuffers();
    public ArraySegment<byte>[] GetIoBuffers(int index, int length) => inner.GetIoBuffers(index, length);
    public ref byte GetPinnableMemoryAddress() => ref inner.GetPinnableMemoryAddress();
    public IntPtr AddressOfPinnedMemory() => inner.AddressOfPinnedMemory();

    // Search
    public int IndexOf(int fromIndex, int toIndex, byte value) => inner.IndexOf(fromIndex, toIndex, value);
    public int BytesBefore(byte value) => inner.BytesBefore(value);
    public int BytesBefore(int length, byte value) => inner.BytesBefore(length, value);
    public int BytesBefore(int index, int length, byte value) => inner.BytesBefore(index, length, value);
    public int ForEachByte(IByteProcessor processor) => inner.ForEachByte(processor);
    public int ForEachByte(int index, int length, IByteProcessor processor) => inner.ForEachByte(index, length, processor);
    public int ForEachByteDesc(IByteProcessor processor) => inner.ForEachByteDesc(processor);
    public int ForEachByteDesc(int index, int length, IByteProcessor processor) => inner.ForEachByteDesc(index, length, processor);

    // Encoding
    public string ToString(Encoding encoding) => inner.ToString(encoding);
    public string ToString(int index, int length, Encoding encoding) => inner.ToString(index, length, encoding);
}

public static class DisposableByteBufferExtensions
{
    public static DisposableByteBuffer AsDisposable(this IByteBuffer buffer) => new(buffer);
}
