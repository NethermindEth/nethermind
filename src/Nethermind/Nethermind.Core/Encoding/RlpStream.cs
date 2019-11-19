using System;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Encoding
{
    public class RlpStream
    {
        private static HeaderDecoder _headerDecoder = new HeaderDecoder();
        private static BlockDecoder _blockDecoder = new BlockDecoder();
        private static TransactionDecoder _txDecoder = new TransactionDecoder();
        private static ReceiptDecoder _receiptDecoder = new ReceiptDecoder();
        private static LogEntryDecoder _logEntryDecoder = new LogEntryDecoder();

        protected RlpStream()
        {
        }
        
        public RlpStream(int length)
        {
            Data = new byte[length]; ;
        }

        public RlpStream(byte[] data)
        {
            Data = data;
        }

        public void Encode(Block value)
        {
            _blockDecoder.Encode(this, value);
        }

        public void Encode(BlockHeader value)
        {
            _headerDecoder.Encode(this, value);
        }

        public void Encode(Transaction value)
        {
            _txDecoder.Encode(this, value);
        }

        public void Encode(TxReceipt value)
        {
            _receiptDecoder.Encode(this, value);
        }

        public void Encode(LogEntry value)
        {
            _logEntryDecoder.Encode(this, value);
        }

        public void StartSequence(int contentLength)
        {
            byte prefix;
            if (contentLength < 56)
            {
                prefix = (byte) (192 + contentLength);
                WriteByte(prefix);
            }
            else
            {
                prefix = (byte) (247 + Rlp.LengthOfLength(contentLength));
                WriteByte(prefix);
                WriteEncodedLength(contentLength);
            }
        }

        private void WriteEncodedLength(int value)
        {
            if (value < 1 << 8)
            {
                WriteByte((byte) value);
                return;
            }

            if (value < 1 << 16)
            {
                WriteByte((byte) (value >> 8));
                WriteByte((byte) value);
                return;
            }

            if (value < 1 << 24)
            {
                WriteByte((byte) (value >> 16));
                WriteByte((byte) (value >> 8));
                WriteByte((byte) value);
                return;
            }

            WriteByte((byte) (value >> 24));
            WriteByte((byte) (value >> 16));
            WriteByte((byte) (value >> 8));
            WriteByte((byte) value);
        }

        protected virtual void WriteByte(byte byteToWrite)
        {
            Data[Position++] = byteToWrite;
        }

        protected virtual void Write(Span<byte> bytesToWrite)
        {
            bytesToWrite.CopyTo(Data.AsSpan(Position, bytesToWrite.Length));
            Position += bytesToWrite.Length;
        }
        
        protected virtual string Description => Data.Slice(0, Math.Min(Rlp.DebugMessageContentLength, Length)).ToHexString();

        public byte[] Data { get; }

        public virtual int Position { get; set; }

        public virtual int Length => Data.Length;

        public bool IsSequenceNext()
        {
            return PeekByte() >= 192;
        }

        public void Encode(Keccak keccak)
        {
            if (keccak == null)
            {
                WriteByte(EmptyArrayByte);
            }
            else if (ReferenceEquals(keccak, Keccak.EmptyTreeHash))
            {
                Write(Rlp.OfEmptyTreeHash.Bytes);
            }
            else if (ReferenceEquals(keccak, Keccak.OfAnEmptyString))
            {
                Write(Rlp.OfEmptyStringHash.Bytes);
            }
            else
            {
                WriteByte(160);
                Write(keccak.Bytes);
            }
        }

        public void Encode(Address address)
        {
            if (address == null)
            {
                WriteByte(EmptyArrayByte);
            }
            else
            {
                WriteByte(148);
                Write(address.Bytes);
            }
        }

        public void Encode(Rlp rlp)
        {
            if (rlp == null)
            {
                WriteByte(EmptyArrayByte);
            }
            else
            {
                Write(rlp.Bytes);
            }
        }

        public void Encode(Bloom bloom)
        {
            if (ReferenceEquals(bloom, Bloom.Empty))
            {
                WriteByte(185);
                WriteByte(1);
                WriteByte(0);
                WriteZero(256);
            }
            else if (bloom == null)
            {
                WriteByte(EmptyArrayByte);
            }
            else
            {
                WriteByte(185);
                WriteByte(1);
                WriteByte(0);
                Write(bloom.Bytes);
            }
        }

        protected virtual void WriteZero(int length)
        {
            Position += 256;
        }

        public void Encode(byte value)
        {
            if (value == 0)
            {
                WriteByte(128);
            }
            else if (value < 128)
            {
                WriteByte(value);
            }
            else
            {
                WriteByte(129);
                WriteByte(value);
            }
        }

        public void Encode(bool value)
        {
            Encode(value ? (byte) 1 : (byte) 0);
        }

        public void Encode(int value)
        {
            Encode((long) value);
        }

        public void Encode(BigInteger bigInteger, int outputLength = -1)
        {
            Rlp rlp = bigInteger == 0 ? Rlp.OfEmptyByteArray : Rlp.Encode(bigInteger.ToBigEndianByteArray(outputLength));
            Write(rlp.Bytes);
        }

        public void Encode(long value)
        {
            if (value == 0L)
            {
                EncodeEmptyArray();
                return;
            }

            if (value > 0)
            {
                byte byte6 = (byte) (value >> 8);
                byte byte5 = (byte) (value >> 16);
                byte byte4 = (byte) (value >> 24);
                byte byte3 = (byte) (value >> 32);
                byte byte2 = (byte) (value >> 40);
                byte byte1 = (byte) (value >> 48);
                byte byte0 = (byte) (value >> 56);

                if (value < 256L * 256L * 256L * 256L * 256L * 256L * 256L)
                {
                    if (value < 256L * 256L * 256L * 256L * 256L * 256L)
                    {
                        if (value < 256L * 256L * 256L * 256L * 256L)
                        {
                            if (value < 256L * 256L * 256L * 256L)
                            {
                                if (value < 256 * 256 * 256)
                                {
                                    if (value < 256 * 256)
                                    {
                                        if (value < 128)
                                        {
                                            WriteByte((byte) value);
                                            return;
                                        }

                                        if (value < 256)
                                        {
                                            WriteByte(129);
                                            WriteByte((byte) value);
                                            return;
                                        }

                                        WriteByte(130);
                                        WriteByte(byte6);
                                        WriteByte((byte) value);
                                        return;
                                    }
                                    
                                    WriteByte(131);
                                    WriteByte(byte5);
                                    WriteByte(byte6);
                                    WriteByte((byte) value);
                                    return;
                                }

                                WriteByte(132);
                                WriteByte(byte4);
                                WriteByte(byte5);
                                WriteByte(byte6);
                                WriteByte((byte) value);
                                return;
                            }

                            WriteByte(133);
                            WriteByte(byte3);
                            WriteByte(byte4);
                            WriteByte(byte5);
                            WriteByte(byte6);
                            WriteByte((byte) value);
                            return;
                        }

                        WriteByte(134);
                        WriteByte(byte2);
                        WriteByte(byte3);
                        WriteByte(byte4);
                        WriteByte(byte5);
                        WriteByte(byte6);
                        WriteByte((byte) value);
                        return;
                    }

                    WriteByte(135);
                    WriteByte(byte1);
                    WriteByte(byte2);
                    WriteByte(byte3);
                    WriteByte(byte4);
                    WriteByte(byte5);
                    WriteByte(byte6);
                    WriteByte((byte) value);
                    return;
                }

                WriteByte(136);
                WriteByte(byte0);
                WriteByte(byte1);
                WriteByte(byte2);
                WriteByte(byte3);
                WriteByte(byte4);
                WriteByte(byte5);
                WriteByte(byte6);
                WriteByte((byte) value);
                return;
            }

            Encode(new BigInteger(value), 8);
        }

        public void Encode(ulong value)
        {
            Encode(value, 8);
        }

        public void Encode(UInt256 value, int length = -1)
        {
            if (value.IsZero && length == -1)
            {
                WriteByte(EmptyArrayByte);
            }
            else
            {
                Span<byte> bytes = stackalloc byte[32];
                value.ToBigEndian(bytes);
                if (length != -1)
                {
                    Encode(bytes.Slice(bytes.Length - length, length));
                }
                else
                {
                    Encode(bytes.WithoutLeadingZeros());
                }
            }
        }

        public void Encode(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                WriteByte(128);
            }
            else
            {
                // todo: can avoid allocation here but benefit is rare
                Encode(System.Text.Encoding.ASCII.GetBytes(value));
            }
        }

        public void Encode(Span<byte> input)
        {
            if (input == null || input.Length == 0)
            {
                WriteByte(EmptyArrayByte);
            }
            else if (input.Length == 1 && input[0] < 128)
            {
                WriteByte(input[0]);
            }
            else if (input.Length < 56)
            {
                byte smallPrefix = (byte) (input.Length + 128);
                WriteByte(smallPrefix);
                Write(input);
            }
            else
            {
                int lengthOfLength = Rlp.LengthOfLength(input.Length);
                byte prefix = (byte) (183 + lengthOfLength);
                WriteByte(prefix);
                WriteEncodedLength(input.Length);
                Write(input);
            }
        }

        public int ReadNumberOfItemsRemaining(int? beforePosition = null)
        {
            int positionStored = Position;
            int numberOfItems = 0;
            while (Position < (beforePosition ?? Length))
            {
                int prefix = ReadByte();
                if (prefix <= 128)
                {
                }
                else if (prefix <= 183)
                {
                    int length = prefix - 128;
                    SkipBytes(length);
                }
                else if (prefix < 192)
                {
                    int lengthOfLength = prefix - 183;
                    int length = DeserializeLength(lengthOfLength);
                    if (length < 56)
                    {
                        throw new RlpException("Expected length greater or equal 56 and was {length}");
                    }

                    SkipBytes(length);
                }
                else
                {
                    Position--;
                    int sequenceLength = ReadSequenceLength();
                    SkipBytes(sequenceLength);
                }

                numberOfItems++;
            }

            Position = positionStored;
            return numberOfItems;
        }

        public void SkipLength()
        {
            SkipBytes(PeekPrefixAndContentLength().PrefixLength);
        }

        public int PeekNextRlpLength()
        {
            (int a, int b) = PeekPrefixAndContentLength();
            return a + b;
        }

        public (int PrefixLength, int ContentLength) PeekPrefixAndContentLength()
        {
            int memorizedPosition = Position;
            (int prefixLength, int contentLengt) result;
            int prefix = ReadByte();
            if (prefix <= 128)
            {
                result = (0, 1);
            }
            else if (prefix <= 183)
            {
                result = (1, prefix - 128);
            }
            else if (prefix < 192)
            {
                int lengthOfLength = prefix - 183;
                if (lengthOfLength > 4)
                {
                    // strange but needed to pass tests - seems that spec gives int64 length and tests int32 length
                    throw new RlpException("Expected length of length less or equal 4");
                }

                int length = DeserializeLength(lengthOfLength);
                if (length < 56)
                {
                    throw new RlpException("Expected length greater or equal 56 and was {length}");
                }

                result = (lengthOfLength + 1, length);
            }
            else if (prefix <= 247)
            {
                result = (1, prefix - 192);
            }
            else
            {
                int lengthOfContentLength = prefix - 247;
                int contentLength = DeserializeLength(lengthOfContentLength);
                if (contentLength < 56)
                {
                    throw new RlpException($"Expected length greater or equal 56 and got {contentLength}");
                }


                result = (lengthOfContentLength + 1, contentLength);
            }

            Position = memorizedPosition;
            return result;
        }

        public int ReadSequenceLength()
        {
            int prefix = ReadByte();
            if (prefix < 192)
            {
                throw new RlpException($"Expected a sequence prefix to be in the range of <192, 255> and got {prefix} at position {Position} in the message of length {Length} starting with {Description}");
            }

            if (prefix <= 247)
            {
                return prefix - 192;
            }

            int lengthOfContentLength = prefix - 247;
            int contentLength = DeserializeLength(lengthOfContentLength);
            if (contentLength < 56)
            {
                throw new RlpException($"Expected length greater or equal 56 and got {contentLength}");
            }

            return contentLength;
        }

        private int DeserializeLength(int lengthOfLength)
        {
            int result;
            if (PeekByte() == 0)
            {
                throw new RlpException("Length starts with 0");
            }

            if (lengthOfLength == 1)
            {
                result = PeekByte();
            }
            else if (lengthOfLength == 2)
            {
                result = PeekByte(1) | (PeekByte() << 8);
            }
            else if (lengthOfLength == 3)
            {
                result = PeekByte(2) | (PeekByte(1) << 8) | (PeekByte()<< 16);
            }
            else if (lengthOfLength == 4)
            {
                result = PeekByte(3) | (PeekByte(2) << 8) | (PeekByte(1) << 16) |
                         (PeekByte() << 24);
            }
            else
            {
                // strange but needed to pass tests - seems that spec gives int64 length and tests int32 length
                throw new InvalidOperationException($"Invalid length of length = {lengthOfLength}");
            }

            SkipBytes(lengthOfLength);
            return result;
        }

        public virtual byte ReadByte()
        {
            return Data[Position++];
        }
        
        protected virtual byte PeekByte()
        {
            return Data[Position];
        }
        
        protected virtual byte PeekByte(int offset)
        {
            return Data[Position + offset];
        }
        
        protected virtual void SkipBytes(int length)
        {
            Position += length;
        }

        protected virtual Span<byte> Read(int length)
        {
            Span<byte> data = Data.AsSpan(Position, length);
            Position += length;
            return data;
        }

        public void Check(int nextCheck)
        {
            if (Position != nextCheck)
            {
                throw new RlpException($"Data checkpoint failed. Expected {nextCheck} and is {Position}");
            }
        }

        public Keccak DecodeKeccak()
        {
            int prefix = ReadByte();
            if (prefix == 128)
            {
                return null;
            }

            if (prefix != 128 + 32)
            {
                throw new RlpException($"Unexpected prefix of {prefix} when decoding {nameof(Keccak)} at position {Position} in the message of length {Length} starting with {Description}");
            }

            Span<byte> keccakSpan = Read(32);
            if (keccakSpan.SequenceEqual(Keccak.OfAnEmptyString.Bytes))
            {
                return Keccak.OfAnEmptyString;
            }

            if (keccakSpan.SequenceEqual(Keccak.EmptyTreeHash.Bytes))
            {
                return Keccak.EmptyTreeHash;
            }

            return new Keccak(keccakSpan.ToArray());
        }

        public Address DecodeAddress()
        {
            int prefix = ReadByte();
            if (prefix == 128)
            {
                return null;
            }

            if (prefix != 128 + 20)
            {
                throw new RlpException($"Unexpected prefix of {prefix} when decoding {nameof(Keccak)} at position {Position} in the message of length {Length} starting with {Description}");
            }

            byte[] buffer = Read(20).ToArray();
            return new Address(buffer);
        }

        public UInt256 DecodeUInt256()
        {
            byte byteValue = PeekByte();
            if (byteValue < 128)
            {
                SkipBytes(1);
                return byteValue;
            }

            Span<byte> byteSpan = DecodeByteArraySpan();
            if (byteSpan.Length > 32)
            {
                throw new ArgumentException();
            }

            UInt256.CreateFromBigEndian(out UInt256 result, byteSpan);
            return result;
        }

        public UInt256? DecodeNullableUInt256()
        {
            if (PeekByte() == 0)
            {
                Position++;
                return null;
            }

            return DecodeUInt256();
        }

        public BigInteger DecodeUBigInt()
        {
            Span<byte> bytes = DecodeByteArraySpan();
            return bytes.ToUnsignedBigInteger();
        }

        public Bloom DecodeBloom()
        {
            Span<byte> bloomBytes;

            // tks: not sure why but some nodes send us Blooms in a sequence form
            // https://github.com/NethermindEth/nethermind/issues/113
            if (PeekByte() == 249)
            {
                SkipBytes(5); // tks: skip 249 1 2 129 127 and read 256 bytes 
                bloomBytes = Read(256);
            }
            else
            {
                bloomBytes = DecodeByteArraySpan();
                if (bloomBytes.Length == 0)
                {
                    return null;
                }
            }

            if (bloomBytes.Length != 256)
            {
                throw new InvalidOperationException("Incorrect bloom RLP");
            }

            if (bloomBytes.SequenceEqual(Extensions.Bytes.Zero256))
            {
                return Bloom.Empty;
            }

            return new Bloom(bloomBytes.ToBigEndianBitArray2048());
        }

        public Span<byte> PeekNextItem()
        {
            int length = PeekNextRlpLength();
            Span<byte> item = Read(length);
            Position -= item.Length;
            return item;
        }

        public bool IsNextItemNull()
        {
            return PeekByte() == 192;
        }

        public bool DecodeBool()
        {
            int prefix = ReadByte();
            if (prefix <= 128)
            {
                return prefix == 1;
            }

            if (prefix <= 183)
            {
                int length = prefix - 128;
                if (length == 1 && PeekByte() < 128)
                {
                    throw new RlpException($"Unexpected byte value {PeekByte()}");
                }

                bool result = PeekByte() == 1;
                SkipBytes(length);
                return result;
            }

            if (prefix < 192)
            {
                int lengthOfLength = prefix - 183;
                if (lengthOfLength > 4)
                {
                    // strange but needed to pass tests - seems that spec gives int64 length and tests int32 length
                    throw new RlpException("Expected length of length less or equal 4");
                }

                int length = DeserializeLength(lengthOfLength);
                if (length < 56)
                {
                    throw new RlpException("Expected length greater or equal 56 and was {length}");
                }

                bool result = PeekByte() == 1;
                SkipBytes(length);
                return result;
            }

            throw new RlpException($"Unexpected prefix of {prefix} when decoding a byte array at position {Position} in the message of length {Length} starting with {Description}");
        }

        public T[] DecodeArray<T>(Func<RlpStream, T> decodeItem, bool checkPositions = true)
        {
            int positionCheck = ReadSequenceLength() + Position;
            int count = ReadNumberOfItemsRemaining(checkPositions ? positionCheck : (int?)null);
            T[] result = new T[count];
            for (int i = 0; i < result.Length; i++)
            {
                if (PeekByte() == Rlp.OfEmptySequence[0])
                {
                    result[i] = default;
                    Position++;
                }
                else
                {
                    result[i] = (T) decodeItem(this);
                }
            }

            return result;
        }

        public string DecodeString()
        {
            Span<byte> bytes = DecodeByteArraySpan();
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        public byte DecodeByte()
        {
            byte byteValue = PeekByte();
            if (byteValue < 128)
            {
                SkipBytes(1);
                return byteValue;
            }
            
            Span<byte> bytes = DecodeByteArraySpan();
            return bytes.Length == 0 ? (byte) 0
                : bytes.Length == 1 ? bytes[0] == (byte) 128
                    ? (byte) 0
                    : bytes[0]
                : bytes[1];
        }

        public int DecodeInt()
        {
            int prefix = ReadByte();
            if (prefix < 128)
            {
                return prefix;
            }

            if (prefix == 128)
            {
                return 0;
            }

            int length = prefix - 128;
            if (length > 4)
            {
                throw new RlpException($"Unexpected length of int value: {length}");
            }

            int result = 0;
            for (int i = 4; i > 0; i--)
            {
                result = result << 8;
                if (i <= length)
                {
                    result = result | PeekByte(length - i);
                }
            }

            SkipBytes(length);

            return result;
        }

        public uint DecodeUInt()
        {
            Span<byte> bytes = DecodeByteArraySpan();
            return bytes.Length == 0 ? 0 : bytes.ReadEthUInt32();
        }

        public long DecodeLong()
        {
            int prefix = ReadByte();
            if (prefix < 128)
            {
                return prefix;
            }

            if (prefix == 128)
            {
                return 0;
            }

            int length = prefix - 128;
            if (length > 8)
            {
                throw new RlpException($"Unexpected length of long value: {length}");
            }

            long result = 0;
            for (int i = 8; i > 0; i--)
            {
                result = result << 8;
                if (i <= length)
                {
                    result = result | PeekByte(length - i);
                }
            }

            SkipBytes(length);

            return result;
        }

        public ulong DecodeUlong()
        {
            Span<byte> bytes = DecodeByteArraySpan();
            return bytes.Length == 0 ? 0L : bytes.ReadEthUInt64();
        }

        public byte[] DecodeByteArray()
        {
            return DecodeByteArraySpan().ToArray();
        }

        public Span<byte> DecodeByteArraySpan()
        {
            int prefix = ReadByte();
            if (prefix == 0)
            {
                return new byte[] {0};
            }

            if (prefix < 128)
            {
                return new[] {(byte) prefix};
            }

            if (prefix == 128)
            {
                return Bytes.Empty;
            }

            if (prefix <= 183)
            {
                int length = prefix - 128;
                Span<byte> buffer = Read(length);
                if (length == 1 && buffer[0] < 128)
                {
                    throw new RlpException($"Unexpected byte value {buffer[0]}");
                }

                return buffer;
            }

            if (prefix < 192)
            {
                int lengthOfLength = prefix - 183;
                if (lengthOfLength > 4)
                {
                    // strange but needed to pass tests - seems that spec gives int64 length and tests int32 length
                    throw new RlpException("Expected length of lenth less or equal 4");
                }

                int length = DeserializeLength(lengthOfLength);
                if (length < 56)
                {
                    throw new RlpException("Expected length greater or equal 56 and was {length}");
                }

                return Read(length);
            }

            throw new RlpException($"Unexpected prefix value of {prefix} when decoding a byte array.");
        }

        public void SkipItem()
        {
            (int prefix, int content) = PeekPrefixAndContentLength();
            SkipBytes(prefix + content);
        }

        public void Reset()
        {
            Position = 0;
        }

        public void EncodeNullObject()
        {
            WriteByte(EmptySequenceByte);
        }
        
        public void EncodeEmptyArray()
        {
            WriteByte(EmptyArrayByte);
        }

        private const byte EmptyArrayByte = 128;
        
        private const byte EmptySequenceByte = 192;
    }
}