// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Serialization.Rlp;

public partial class Rlp
{
    public static readonly Rlp OfEmptyByteArray = new(EmptyByteArrayByte);

    public static readonly Rlp OfZero = OfEmptyByteArray;

    public static readonly Rlp OfEmptyList = new(EmptyListByte);

    internal static readonly Rlp OfEmptyTreeHash = Encode(Keccak.EmptyTreeHash.Bytes); // use bytes to avoid stack overflow

    internal static readonly Rlp OfEmptyStringHash = Encode(Keccak.OfAnEmptyString.Bytes); // use bytes to avoid stack overflow

    internal static readonly Rlp EmptyBloom = Encode(Bloom.Empty.Bytes);

    private static readonly Dictionary<RlpDecoderKey, IRlpDecoder> _decoderBuilder = [];
    private static readonly Lock _decoderLock = new();

    private static ILogger _logger = Static.LogManager.GetClassLogger<Rlp>();

    // A field initializer rather than a static constructor keeps the type beforefieldinit, so static methods
    // that read no static field (e.g. LengthOf) need no type initialization check on every call. Declared after
    // every other initialized static field: registration constructs decoders, which may read them.
    private static readonly bool _defaultDecodersRegistered = RegisterDefaultDecoders();

    private static FrozenDictionary<RlpDecoderKey, IRlpDecoder>? _decodersSnapshot;

    public static FrozenDictionary<RlpDecoderKey, IRlpDecoder> Decoders
    {
        get
        {
            FrozenDictionary<RlpDecoderKey, IRlpDecoder>? snapshot = Volatile.Read(ref _decodersSnapshot);
            return snapshot ?? CreateDecodersSnapshot();
        }
    }

    private static FrozenDictionary<RlpDecoderKey, IRlpDecoder> CreateDecodersSnapshot()
    {
        using Lock.Scope _ = _decoderLock.EnterScope();
        FrozenDictionary<RlpDecoderKey, IRlpDecoder>? snapshot = _decodersSnapshot;
        if (snapshot is null)
        {
            snapshot = _decoderBuilder.ToFrozenDictionary();
            Volatile.Write(ref _decodersSnapshot, snapshot);
        }

        return snapshot;
    }

    private static bool RegisterDefaultDecoders()
    {
        RegisterDecoders(typeof(Rlp).Assembly);
        RegisterDecoder(typeof(Transaction), TxDecoder.Instance);
        return true;
    }

    public static partial void RegisterDecoders(Assembly assembly, bool canOverrideExistingDecoders)
    {
        foreach (Type? type in assembly.GetExportedTypes())
        {
            if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition)
            {
                continue;
            }

            if (type.GetCustomAttribute<SkipGlobalRegistration>() is not null)
            {
                continue;
            }

            Type[]? implementedInterfaces = type.GetInterfaces();
            foreach (Type? implementedInterface in implementedInterfaces)
            {
                if (!implementedInterface.IsGenericType)
                {
                    continue;
                }

                Type? interfaceGenericDefinition = implementedInterface.GetGenericTypeDefinition();
                if (interfaceGenericDefinition == typeof(IRlpDecoder<>).GetGenericTypeDefinition())
                {
                    bool isSetForAnyAttribute = false;
                    IRlpDecoder? instance = null;

                    foreach (DecoderAttribute rlpDecoderAttr in type.GetCustomAttributes<DecoderAttribute>())
                    {
                        RlpDecoderKey key = new(implementedInterface.GenericTypeArguments[0], rlpDecoderAttr.Key);
                        AddEncoder(key);

                        isSetForAnyAttribute = true;
                    }

                    if (!isSetForAnyAttribute)
                    {
                        AddEncoder(new(implementedInterface.GenericTypeArguments[0]));
                    }

                    void AddEncoder(RlpDecoderKey key)
                    {
                        using Lock.Scope _ = _decoderLock.EnterScope();
                        if (!_decoderBuilder.TryGetValue(key, out IRlpDecoder? value) || canOverrideExistingDecoders)
                        {
                            try
                            {
                                object? decoder = type.GetConstructor(Type.EmptyTypes) is not null
                                    ? Activator.CreateInstance(type)
                                    : Activator.CreateInstance(type, BindingFlags.CreateInstance | BindingFlags.OptionalParamBinding, null, [Type.Missing], null);
                                _decoderBuilder[key] = instance ??= (IRlpDecoder)(decoder
                                    ?? throw new InvalidOperationException($"Could not create decoder {type}."));
                            }
                            catch (Exception)
                            {
                                throw new ArgumentException($"Unable to set decoder for {key}, because {type} decoder has no suitable constructor.");
                            }
                        }
                        else
                        {
                            throw new InvalidOperationException($"Unable to override decoder for {key}, because the following decoder is already set: {value}.");
                        }
                    }
                }
            }
        }

        Volatile.Write(ref _decodersSnapshot, null);
    }
}

public readonly partial struct RlpDecoderKey
{
    public override int GetHashCode() => HashCode.Combine(_type, MemoryMarshal.AsBytes(_key.AsSpan()).FastHash());
}
