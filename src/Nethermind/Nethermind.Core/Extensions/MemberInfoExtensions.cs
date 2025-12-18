// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using Nethermind.Int256;

namespace Nethermind.Core.Extensions;

/// <summary>
/// Extension methods for <see cref="MemberInfo"/> that generate fast, cached accessors
/// for reading (and in some cases writing) static fields and properties.
/// </summary>
public static class MemberInfoExtensions
{
    /// <summary>
    /// Cache is keyed by (member, result type) because the compiled delegate's return type
    /// depends on the generic parameter <typeparamref name="T"/>.
    /// </summary>
    private static readonly ConcurrentDictionary<CacheKey, Delegate> _cache = new();

    /// <summary>
    /// Creates (or retrieves from cache) a compiled accessor delegate for a static field or property.
    /// Supports special handling for <c>Nullable&lt;T&gt;</c> and <c>UInt256</c>.
    /// </summary>
    public static Func<T> GetValueAccessor<T>(this MemberInfo memberInfo)
    {
        ArgumentNullException.ThrowIfNull(memberInfo);

        return (Func<T>)_cache.GetOrAdd(new CacheKey(memberInfo, typeof(T)), static key =>
        {
            return BuildAccessor<T>(key.Member);
        });
    }

    /// <summary>
    /// Builds a compiled lambda that reads the value of the given field or property.
    /// </summary>
    private static Func<T> BuildAccessor<T>(this MemberInfo memberInfo)
    {
        if (memberInfo is PropertyInfo property)
        {
            MethodInfo getter = property.GetMethod ?? throw new NotSupportedException("Property has no getter.");
            if (!getter.IsStatic) throw new NotSupportedException("Only static properties are supported.");

            Expression body = Expression.Property(null, property);
            body = NormalizeBody(body);

            if (body.Type != typeof(T))
                body = Expression.Convert(body, typeof(T));

            return Expression.Lambda<Func<T>>(
                body,
                name: $"Get_{property.DeclaringType?.Name}_{property.Name}",
                parameters: null
            ).Compile();
        }
        else if (memberInfo is FieldInfo field)
        {
            if (!field.IsStatic) throw new NotSupportedException("Only static fields are supported.");

            Expression body = Expression.Field(null, field);
            body = NormalizeBody(body);

            if (body.Type != typeof(T))
                body = Expression.Convert(body, typeof(T));

            return Expression.Lambda<Func<T>>(
                body,
                name: $"Get_{field.DeclaringType?.Name}_{field.Name}",
                parameters: null
            ).Compile();
        }

        throw new NotSupportedException("Should be used for field and property only");
    }

    /// <summary>
    /// Normalizes an expression for value access:
    /// - If the type is <c>Nullable&lt;U&gt;</c>, replaces it with a call to <c>GetValueOrDefault()</c>.
    /// - If the type is <c>UInt256</c>, replaces it with a call to <c>.ToDouble(null)</c>.
    /// </summary>
    private static Expression NormalizeBody(Expression body)
    {
        // Unwrap Nullable<U> with GetValueOrDefault()
        Type? underlying = Nullable.GetUnderlyingType(body.Type);
        if (underlying != null)
        {
            MethodInfo getValueOrDefault = body.Type.GetMethod(nameof(Nullable<int>.GetValueOrDefault), Type.EmptyTypes)
                ?? throw new NotSupportedException($"{body.Type} is nullable but {nameof(Nullable<int>.GetValueOrDefault)}() not found.");
            body = Expression.Call(body, getValueOrDefault);
        }

        // Special-case UInt256 -> double conversion
        if (body.Type == typeof(UInt256))
        {
            body = Expression.Call(
                body,
                nameof(UInt256.ToDouble),
                typeArguments: null,
                Expression.Constant(null, typeof(IFormatProvider)) // pass null provider
            );
        }

        return body;
    }

    /// <summary>
    /// Directly gets the current value of a static field or property.
    /// Uses the cached accessor for performance.
    /// </summary>
    public static T GetValue<T>(this MemberInfo memberInfo) => GetValueAccessor<T>(memberInfo)();

    /// <summary>
    /// Gets the declared type of a field or property.
    /// </summary>
    public static Type GetMemberType(this MemberInfo memberInfo) =>
        memberInfo switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => throw new NotSupportedException("Should be used for field and property only")
        };

    /// <summary>
    /// Sets the value of a static field or property using reflection.
    /// </summary>
    public static void SetValue(this MemberInfo memberInfo, object value)
    {
        switch (memberInfo)
        {
            case PropertyInfo p:
                p.SetValue(null, value);
                break;
            case FieldInfo f:
                f.SetValue(null, value);
                break;
            default:
                throw new UnreachableException();
        }
    }

    private readonly struct CacheKey(MemberInfo member, Type resultType) : IEquatable<CacheKey>
    {
        public readonly MemberInfo Member = member ?? throw new ArgumentNullException(nameof(member));
        public readonly Type ResultType = resultType ?? throw new ArgumentNullException(nameof(resultType));

        public bool Equals(CacheKey other) =>
            Member.Equals(other.Member) && ResultType.Equals(other.ResultType);

        public override bool Equals(object? obj) =>
            obj is CacheKey other && Equals(other);

        public override int GetHashCode() => Member.GetHashCode() ^ ResultType.GetHashCode();
    }
}
