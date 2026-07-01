// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp;

public partial class Rlp
{
    private static FrozenDictionary<RlpDecoderKey, IRlpDecoder>? _decodersSnapshot;

    public static FrozenDictionary<RlpDecoderKey, IRlpDecoder> Decoders
    {
        get
        {
            // Acquire read paired with the Volatile.Write publish in CreateDecodersSnapshot / invalidations.
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
                                _decoderBuilder[key] = instance ??= (IRlpDecoder)(type.GetConstructor(Type.EmptyTypes) is not null ?
                                    Activator.CreateInstance(type) :
                                    Activator.CreateInstance(type, BindingFlags.CreateInstance | BindingFlags.OptionalParamBinding, null, [Type.Missing], null));
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
