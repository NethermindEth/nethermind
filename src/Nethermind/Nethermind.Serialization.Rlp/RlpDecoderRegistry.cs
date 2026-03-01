// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Nethermind.Serialization.Rlp;

public sealed class RlpDecoderRegistry : IRlpDecoderRegistry
{
    private readonly FrozenDictionary<RlpDecoderKey, IRlpDecoder> _decoders;

    public RlpDecoderRegistry(FrozenDictionary<RlpDecoderKey, IRlpDecoder> decoders) =>
        _decoders = decoders;

    public IRlpValueDecoder<T>? GetValueDecoder<T>(string key = RlpDecoderKey.Default) =>
        _decoders.TryGetValue(new(typeof(T), key), out IRlpDecoder value) ? value as IRlpValueDecoder<T> : null;

    public IRlpStreamEncoder<T>? GetStreamEncoder<T>(string key = RlpDecoderKey.Default) =>
        _decoders.TryGetValue(new(typeof(T), key), out IRlpDecoder value) ? value as IRlpStreamEncoder<T> : null;

    public IRlpObjectDecoder<T> GetObjectDecoder<T>(string key = RlpDecoderKey.Default) =>
        _decoders.GetValueOrDefault(new(typeof(T), key)) as IRlpObjectDecoder<T>
        ?? throw new RlpException($"{nameof(Rlp)} does not support encoding {typeof(T).Name}");
}

public sealed class RlpDecoderRegistryBuilder
{
    private readonly Dictionary<RlpDecoderKey, IRlpDecoder> _decoders = new();

    public RlpDecoderRegistryBuilder RegisterDecoder(RlpDecoderKey key, IRlpDecoder decoder)
    {
        _decoders[key] = decoder;
        return this;
    }

#if !ZKVM
    public RlpDecoderRegistryBuilder RegisterDecoders(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)]
        Assembly? assembly,
        bool canOverrideExistingDecoders = false)
    {
        if (assembly is null) return this;

        foreach (Type? type in assembly.GetExportedTypes())
        {
            if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition)
                continue;

            if (type.GetCustomAttribute<Rlp.SkipGlobalRegistration>() is not null)
                continue;

            Type[]? implementedInterfaces = type.GetInterfaces();
            foreach (Type? implementedInterface in implementedInterfaces)
            {
                if (!implementedInterface.IsGenericType)
                    continue;

                Type? interfaceGenericDefinition = implementedInterface.GetGenericTypeDefinition();
                if (interfaceGenericDefinition == typeof(IRlpDecoder<>).GetGenericTypeDefinition())
                {
                    bool isSetForAnyAttribute = false;
                    IRlpDecoder? instance = null;

                    foreach (Rlp.DecoderAttribute rlpDecoderAttr in type.GetCustomAttributes<Rlp.DecoderAttribute>())
                    {
                        RlpDecoderKey key = new(implementedInterface.GenericTypeArguments[0], rlpDecoderAttr.Key);
                        AddEncoder(key, type, ref instance, canOverrideExistingDecoders);
                        isSetForAnyAttribute = true;
                    }

                    if (!isSetForAnyAttribute)
                    {
                        AddEncoder(new(implementedInterface.GenericTypeArguments[0]), type, ref instance, canOverrideExistingDecoders);
                    }
                }
            }
        }

        return this;
    }

    private void AddEncoder(RlpDecoderKey key, Type type, ref IRlpDecoder? instance, bool canOverrideExistingDecoders)
    {
        if (!_decoders.TryGetValue(key, out IRlpDecoder? value) || canOverrideExistingDecoders)
        {
            try
            {
                _decoders[key] = instance ??= (IRlpDecoder)(type.GetConstructor(Type.EmptyTypes) is not null
                    ? Activator.CreateInstance(type)
                    : Activator.CreateInstance(type, BindingFlags.CreateInstance | BindingFlags.OptionalParamBinding, null, [Type.Missing], null))!;
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
#endif

    public RlpDecoderRegistry Build() => new(_decoders.ToFrozenDictionary());
}
