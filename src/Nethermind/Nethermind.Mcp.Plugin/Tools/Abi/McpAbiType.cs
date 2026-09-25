// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Mcp.Plugin.Tools.Abi;

/// <summary>The kind of an ABI type.</summary>
public enum McpAbiTypeKind
{
    /// <summary><c>address</c>.</summary>
    Address,

    /// <summary><c>bool</c>.</summary>
    Bool,

    /// <summary><c>uint8</c> to <c>uint256</c>.</summary>
    UInt,

    /// <summary><c>int8</c> to <c>int256</c>.</summary>
    Int,

    /// <summary><c>bytes1</c> to <c>bytes32</c>.</summary>
    FixedBytes,

    /// <summary>Dynamic <c>bytes</c>.</summary>
    Bytes,

    /// <summary><c>string</c>.</summary>
    String,

    /// <summary>A dynamic array <c>T[]</c>.</summary>
    Array,

    /// <summary>A fixed-length array <c>T[k]</c>.</summary>
    FixedArray,

    /// <summary>A tuple (struct) <c>(T1,T2,...)</c>.</summary>
    Tuple
}

/// <summary>A named ABI parameter; <see cref="Indexed"/> only applies to event inputs.</summary>
/// <param name="Name">The parameter name, or an empty string if unnamed.</param>
/// <param name="Type">The parameter type.</param>
/// <param name="Indexed">Whether the event parameter is stored in a topic.</param>
public sealed record McpAbiParam(string Name, McpAbiType Type, bool Indexed = false);

/// <summary>An ABI type as used by the signature codec (the Solidity ABI v2 subset without fixed-point and function types).</summary>
public sealed class McpAbiType
{
    /// <summary>The maximum nesting of arrays and tuples accepted in a type.</summary>
    public const int MaxNestingDepth = 8;

    /// <summary>The maximum static encoded size of a type, which bounds huge fixed arrays such as <c>uint256[1000][1000]</c>.</summary>
    public const int MaxStaticSize = 32 * 4096;

    /// <summary>The maximum number of components of one tuple.</summary>
    public const int MaxTupleComponents = 64;

    private const int WordSize = 32;

    private McpAbiType(McpAbiTypeKind kind, int size, McpAbiType? element, IReadOnlyList<McpAbiParam>? components)
    {
        Kind = kind;
        Size = size;
        Element = element;
        Components = components ?? [];
        CanonicalName = BuildName();
        IsDynamic = kind switch
        {
            McpAbiTypeKind.Bytes or McpAbiTypeKind.String or McpAbiTypeKind.Array => true,
            McpAbiTypeKind.FixedArray => element!.IsDynamic,
            McpAbiTypeKind.Tuple => Components.Any(static c => c.Type.IsDynamic),
            _ => false
        };

        long staticSize = kind switch
        {
            _ when IsDynamic => WordSize,
            McpAbiTypeKind.FixedArray => (long)size * element!.HeadSize,
            McpAbiTypeKind.Tuple => Components.Sum(static c => (long)c.Type.HeadSize),
            _ => WordSize
        };

        if (staticSize > MaxStaticSize)
        {
            throw new FormatException($"type '{CanonicalName}' is too large: its static encoding exceeds {MaxStaticSize} bytes");
        }

        HeadSize = (int)staticSize;
        Depth = kind switch
        {
            McpAbiTypeKind.Array or McpAbiTypeKind.FixedArray => element!.Depth + 1,
            McpAbiTypeKind.Tuple => 1 + (Components.Count == 0 ? 0 : Components.Max(static c => c.Type.Depth)),
            _ => 0
        };

        if (Depth > MaxNestingDepth)
        {
            throw new FormatException($"type '{CanonicalName}' nests arrays/tuples deeper than {MaxNestingDepth} levels");
        }
    }

    /// <summary>Gets <c>address</c>.</summary>
    public static McpAbiType Address { get; } = new(McpAbiTypeKind.Address, 160, null, null);

    /// <summary>Gets <c>bool</c>.</summary>
    public static McpAbiType Bool { get; } = new(McpAbiTypeKind.Bool, 8, null, null);

    /// <summary>Gets <c>uint256</c>.</summary>
    public static McpAbiType UInt256 { get; } = new(McpAbiTypeKind.UInt, 256, null, null);

    /// <summary>Gets <c>uint8</c>.</summary>
    public static McpAbiType UInt8 { get; } = new(McpAbiTypeKind.UInt, 8, null, null);

    /// <summary>Gets <c>bytes32</c>.</summary>
    public static McpAbiType Bytes32 { get; } = new(McpAbiTypeKind.FixedBytes, 32, null, null);

    /// <summary>Gets dynamic <c>bytes</c>.</summary>
    public static McpAbiType Bytes { get; } = new(McpAbiTypeKind.Bytes, 0, null, null);

    /// <summary>Gets <c>string</c>.</summary>
    public static McpAbiType String { get; } = new(McpAbiTypeKind.String, 0, null, null);

    /// <summary>Gets the kind of this type.</summary>
    public McpAbiTypeKind Kind { get; }

    /// <summary>Gets the bit width of integers, the byte length of <c>bytesN</c>, or the length of a fixed array.</summary>
    public int Size { get; }

    /// <summary>Gets the element type of an array.</summary>
    public McpAbiType? Element { get; }

    /// <summary>Gets the components of a tuple (empty for other kinds).</summary>
    public IReadOnlyList<McpAbiParam> Components { get; }

    /// <summary>Gets the canonical type name used in signatures, such as <c>(address,uint256)[]</c>.</summary>
    public string CanonicalName { get; }

    /// <summary>Gets whether the type is dynamically sized in the ABI encoding.</summary>
    public bool IsDynamic { get; }

    /// <summary>Gets the size of the type's head in a sequence encoding: 32 for dynamic types, else the full static size.</summary>
    public int HeadSize { get; }

    /// <summary>Gets the nesting depth of arrays and tuples.</summary>
    public int Depth { get; }

    /// <summary>Creates <c>uintN</c>.</summary>
    public static McpAbiType UInt(int bits) => bits == 256 ? UInt256 : new(McpAbiTypeKind.UInt, CheckBits(bits), null, null);

    /// <summary>Creates <c>intN</c>.</summary>
    public static McpAbiType Int(int bits) => new(McpAbiTypeKind.Int, CheckBits(bits), null, null);

    /// <summary>Creates <c>bytesN</c>.</summary>
    public static McpAbiType FixedBytes(int length) => length is >= 1 and <= 32
        ? new(McpAbiTypeKind.FixedBytes, length, null, null)
        : throw new FormatException($"bytes{length} is not a valid type; bytesN needs 1 <= N <= 32");

    /// <summary>Creates <c>T[]</c>.</summary>
    public static McpAbiType ArrayOf(McpAbiType element) => new(McpAbiTypeKind.Array, 0, element, null);

    /// <summary>Creates <c>T[length]</c>.</summary>
    public static McpAbiType FixedArrayOf(McpAbiType element, int length) => length >= 1
        ? new(McpAbiTypeKind.FixedArray, length, element, null)
        : throw new FormatException("fixed array length must be at least 1");

    /// <summary>Creates a tuple of <paramref name="components"/>.</summary>
    public static McpAbiType TupleOf(IReadOnlyList<McpAbiParam> components) => components.Count is >= 1 and <= MaxTupleComponents
        ? new(McpAbiTypeKind.Tuple, components.Count, null, components)
        : throw new FormatException($"a tuple needs 1 to {MaxTupleComponents} components");

    /// <inheritdoc/>
    public override string ToString() => CanonicalName;

    private static int CheckBits(int bits) => bits is >= 8 and <= 256 && bits % 8 == 0
        ? bits
        : throw new FormatException($"integer width {bits} is not valid; use a multiple of 8 between 8 and 256");

    private string BuildName()
    {
        switch (Kind)
        {
            case McpAbiTypeKind.Address: return "address";
            case McpAbiTypeKind.Bool: return "bool";
            case McpAbiTypeKind.UInt: return $"uint{Size}";
            case McpAbiTypeKind.Int: return $"int{Size}";
            case McpAbiTypeKind.FixedBytes: return $"bytes{Size}";
            case McpAbiTypeKind.Bytes: return "bytes";
            case McpAbiTypeKind.String: return "string";
            case McpAbiTypeKind.Array: return $"{Element!.CanonicalName}[]";
            case McpAbiTypeKind.FixedArray: return $"{Element!.CanonicalName}[{Size}]";
            default:
                StringBuilder builder = new("(");
                for (int i = 0; i < Components.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    builder.Append(Components[i].Type.CanonicalName);
                }

                return builder.Append(')').ToString();
        }
    }
}
