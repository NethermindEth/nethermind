// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using System;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp;

[Rlp.SkipGlobalRegistration]
public sealed class ReceiptArrayStorageDecoder : RlpValueDecoder<TxReceipt[]>
{
    public static readonly ReceiptArrayStorageDecoder Instance = new();

    private readonly bool _compactEncoding;
    private readonly IRlpStreamEncoder<TxReceipt> _decoder;
    private readonly IRlpValueDecoder<TxReceipt> _valueDecoder;
    private readonly IRlpStreamEncoder<TxReceipt> _compactDecoder;
    private readonly IRlpValueDecoder<TxReceipt> _compactValueDecoder;

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ReceiptArrayStorageDecoder))]
    public ReceiptArrayStorageDecoder() : this(true) { }

    public ReceiptArrayStorageDecoder(bool compactEncoding) : this(compactEncoding, Rlp.DefaultRegistry) { }

    public ReceiptArrayStorageDecoder(bool compactEncoding, IRlpDecoderRegistry registry)
    {
        _compactEncoding = compactEncoding;
        _decoder = registry.GetStreamEncoder<TxReceipt>(RlpDecoderKey.LegacyStorage)!;
        _valueDecoder = registry.GetValueDecoder<TxReceipt>(RlpDecoderKey.LegacyStorage)!;
        _compactDecoder = registry.GetStreamEncoder<TxReceipt>(RlpDecoderKey.Storage)!;
        _compactValueDecoder = registry.GetValueDecoder<TxReceipt>(RlpDecoderKey.Storage)!;
    }

    public const int CompactEncoding = 127;

    public override int GetLength(TxReceipt[] items, RlpBehaviors rlpBehaviors)
    {
        if (items is null || items.Length == 0)
        {
            return 1;
        }

        int bufferLength = Rlp.LengthOfSequence(GetContentLength(items, rlpBehaviors));
        if (_compactEncoding && (rlpBehaviors & RlpBehaviors.Storage) != 0)
        {
            bufferLength++;
        }
        return bufferLength;
    }

    private int GetContentLength(TxReceipt[] items, RlpBehaviors rlpBehaviors)
    {
        if (_compactEncoding && (rlpBehaviors & RlpBehaviors.Storage) != 0)
        {
            int totalLength = 0;
            for (int i = 0; i < items.Length; i++)
            {
                totalLength += _compactDecoder.GetLength(items[i], rlpBehaviors);
            }

            return totalLength;
        }
        else
        {
            int totalLength = 0;
            for (int i = 0; i < items.Length; i++)
            {
                totalLength += _decoder.GetLength(items[i], rlpBehaviors);
            }

            return totalLength;
        }
    }

    protected override TxReceipt[] DecodeInternal(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.PeekByte() == CompactEncoding)
        {
            decoderContext.ReadByte();
            return _compactValueDecoder.DecodeArray(ref decoderContext, RlpBehaviors.Storage | RlpBehaviors.AllowExtraBytes);
        }
        else
        {
            try
            {
                return _valueDecoder.DecodeArray(ref decoderContext, RlpBehaviors.Storage);
            }
            catch (RlpException)
            {
                decoderContext.Position = 0;
                return _valueDecoder.DecodeArray(ref decoderContext);
            }
        }
    }

    public override void Encode(RlpStream stream, TxReceipt[] items, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (items is null || items.Length == 0)
        {
            stream.EncodeNullObject();
            return;
        }

        if (_compactEncoding && (rlpBehaviors & RlpBehaviors.Storage) != 0)
        {
            int totalLength = GetContentLength(items, rlpBehaviors);
            stream.WriteByte(CompactEncoding);
            stream.StartSequence(totalLength - 1);

            for (int i = 0; i < items.Length; i++)
            {
                _compactDecoder.Encode(stream, items[i], rlpBehaviors);
            }
        }
        else
        {
            int totalLength = GetContentLength(items, rlpBehaviors);
            stream.StartSequence(totalLength);

            for (int i = 0; i < items.Length; i++)
            {
                _decoder.Encode(stream, items[i], rlpBehaviors);
            }
        }
    }

    public TxReceipt[] Decode(in Span<byte> receiptsData)
    {
        if (receiptsData.Length == 0 || receiptsData[0] == Rlp.EmptyListByte)
        {
            return [];
        }

        if (receiptsData.Length > 0 && receiptsData[0] == CompactEncoding)
        {
            var decoderContext = new Rlp.ValueDecoderContext(receiptsData[1..]);
            return _compactValueDecoder.DecodeArray(ref decoderContext, RlpBehaviors.Storage | RlpBehaviors.AllowExtraBytes);
        }
        else
        {
            var decoderContext = new Rlp.ValueDecoderContext(receiptsData);
            try
            {
                return _valueDecoder.DecodeArray(ref decoderContext, RlpBehaviors.Storage);
            }
            catch (RlpException)
            {
                decoderContext.Position = 0;
                return _valueDecoder.DecodeArray(ref decoderContext);
            }
        }
    }

    public TxReceipt DeserializeReceiptObsolete(Hash256 hash, Span<byte> receiptData)
    {
        var context = new Rlp.ValueDecoderContext(receiptData);
        try
        {
            var receipt = _valueDecoder.Decode(ref context, RlpBehaviors.Storage);
            receipt.TxHash = hash;
            return receipt;
        }
        catch (RlpException)
        {
            context.Position = 0;
            var receipt = _valueDecoder.Decode(ref context);
            receipt.TxHash = hash;
            return receipt;
        }
    }

    public static bool IsCompactEncoding(Span<byte> receiptsData) => receiptsData.Length > 0 && receiptsData[0] == CompactEncoding;

    public IReceiptRefDecoder GetRefDecoder(Span<byte> receiptsData) =>
        IsCompactEncoding(receiptsData)
            ? (IReceiptRefDecoder)_compactValueDecoder
            : (IReceiptRefDecoder)_valueDecoder;
}
