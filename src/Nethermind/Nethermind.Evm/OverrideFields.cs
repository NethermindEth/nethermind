// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Evm;

/// <summary>The JSON names of an override type's fields, compared ASCII case-insensitively.</summary>
/// <remarks>
/// The spec requires a client to reject an override key that names no supported field, or two keys that name the same
/// field. A key that differs from a field name only in case names that field. Keys are compared as UTF-8 bytes, so a valid
/// key allocates nothing.
/// </remarks>
public sealed class OverrideFields
{
    private readonly byte[][] _names;

    private OverrideFields(byte[][] names)
    {
        // One bit per field and per letter of a name.
        if (names.Length > 64 || names.Any(static name => name.Length > 64 || name.Any(static b => b >= 0x80)))
            throw new InvalidOperationException("The override fields do not fit the key check.");
        _names = names;
    }

    /// <summary>The number of fields.</summary>
    public int Count => _names.Length;

    /// <summary>The fields named by the settable, serialized properties of <typeparamref name="TOverride"/>.</summary>
    public static OverrideFields Of<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TOverride>() =>
        new(typeof(TOverride)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static property => property.SetMethod?.IsPublic == true
                && property.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Select(static property => Encoding.UTF8.GetBytes(property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name))
            .ToArray());

    /// <summary>The fields with the given names; the index of a name is the index of its field.</summary>
    public static OverrideFields Of(params string[] names) =>
        new(names.Select(static name => Encoding.UTF8.GetBytes(name)).ToArray());

    /// <summary>Returns the field that the key at <paramref name="reader"/> names, and marks it in <paramref name="seen"/>.</summary>
    /// <param name="reader">The reader, positioned at a property name.</param>
    /// <param name="seen">The fields that earlier keys of the same object named.</param>
    /// <param name="upperCase">One entry per field: the upper-case letters of the key that named it.</param>
    /// <param name="owner">The override object, to name in an error.</param>
    /// <exception cref="JsonException">The key names no field, or names a field that an earlier key spelled differently.</exception>
    public int Match(in Utf8JsonReader reader, ref ulong seen, Span<ulong> upperCase, string owner)
    {
        int field = Find(reader, out ulong keyUpperCase);
        if (field < 0)
        {
            throw new JsonException($"unknown field \"{reader.GetString()}\" in {owner}");
        }

        // The same spelling twice is one key given twice; only another spelling names the field again.
        ulong bit = 1UL << field;
        if ((seen & bit) != 0 && upperCase[field] != keyUpperCase)
        {
            throw new JsonException($"keys \"{Spell(field, upperCase[field])}\" and \"{reader.GetString()}\" in {owner} name the same field");
        }

        seen |= bit;
        upperCase[field] = keyUpperCase;
        return field;
    }

    /// <summary>The field the key at <paramref name="reader"/> names, or -1; <paramref name="upperCase"/> marks the
    /// positions of the key's upper-case letters, which tell its spellings apart.</summary>
    private int Find(in Utf8JsonReader reader, out ulong upperCase)
    {
        if (reader.ValueIsEscaped || reader.HasValueSequence)
        {
            return Find(Encoding.UTF8.GetBytes(reader.GetString()!), out upperCase);
        }

        return Find(reader.ValueSpan, out upperCase);
    }

    private int Find(ReadOnlySpan<byte> key, out ulong upperCase)
    {
        for (int i = 0; i < _names.Length; i++)
        {
            if (EqualsIgnoreCase(key, _names[i], out upperCase)) return i;
        }

        upperCase = 0;
        return -1;
    }

    /// <summary>Spells field <paramref name="field"/> with the upper-case letters <paramref name="upperCase"/> marks.</summary>
    private string Spell(int field, ulong upperCase)
    {
        byte[] name = _names[field];
        char[] chars = new char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            char c = (char)name[i];
            chars[i] = (upperCase & (1UL << i)) != 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c);
        }

        return new string(chars);
    }

    /// <summary>ASCII case-insensitive equality; <paramref name="upperCase"/> marks the upper-case letters of
    /// <paramref name="key"/>.</summary>
    public static bool EqualsIgnoreCase(ReadOnlySpan<byte> key, ReadOnlySpan<byte> name, out ulong upperCase)
    {
        upperCase = 0;
        if (key.Length != name.Length) return false;

        for (int i = 0; i < key.Length; i++)
        {
            byte k = key[i];
            bool upper = k is >= (byte)'A' and <= (byte)'Z';
            if ((upper ? (byte)(k | 0x20) : k) != (byte)(name[i] | (name[i] is >= (byte)'A' and <= (byte)'Z' ? 0x20 : 0))) return false;
            if (upper) upperCase |= 1UL << i;
        }

        return true;
    }
}
