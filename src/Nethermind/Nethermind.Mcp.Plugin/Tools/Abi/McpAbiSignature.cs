// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Nethermind.Core.Crypto;

namespace Nethermind.Mcp.Plugin.Tools.Abi;

/// <summary>Whether a signature declares a function, an event or an error.</summary>
public enum McpAbiSignatureKind
{
    /// <summary>A function; its selector is the first 4 bytes of the Keccak hash of the canonical signature.</summary>
    Function,

    /// <summary>An event; its topic0 is the Keccak hash of the canonical signature (unless anonymous).</summary>
    Event,

    /// <summary>A custom error; encoded like a function call.</summary>
    Error
}

/// <summary>
/// A parsed human-readable ABI signature, such as <c>balanceOf(address)</c>,
/// <c>function transfer(address to, uint256 amount) returns (bool)</c>, <c>getReserves()(uint112,uint112,uint32)</c> or
/// <c>event Transfer(address indexed from, address indexed to, uint256 value)</c>.
/// </summary>
public sealed class McpAbiSignature
{
    /// <summary>The maximum accepted signature length in characters.</summary>
    public const int MaxLength = 4096;

    private McpAbiSignature(McpAbiSignatureKind kind, string name, IReadOnlyList<McpAbiParam> inputs, IReadOnlyList<McpAbiParam>? outputs, bool anonymous)
    {
        Kind = kind;
        Name = name;
        Inputs = inputs;
        Outputs = outputs;
        Anonymous = anonymous;

        StringBuilder builder = new(name);
        AppendTypes(builder, inputs);
        CanonicalSignature = builder.ToString();
        Hash = Keccak.Compute(CanonicalSignature);
        Selector = Hash.Bytes[..4].ToArray();
    }

    /// <summary>Gets the declaration kind.</summary>
    public McpAbiSignatureKind Kind { get; }

    /// <summary>Gets the function, event or error name.</summary>
    public string Name { get; }

    /// <summary>Gets the inputs (event parameters for events).</summary>
    public IReadOnlyList<McpAbiParam> Inputs { get; }

    /// <summary>Gets the declared return types, or <see langword="null"/> if the signature did not declare any.</summary>
    public IReadOnlyList<McpAbiParam>? Outputs { get; }

    /// <summary>Gets whether the event is anonymous (it has no signature topic).</summary>
    public bool Anonymous { get; }

    /// <summary>Gets the canonical signature, such as <c>transfer(address,uint256)</c>.</summary>
    public string CanonicalSignature { get; }

    /// <summary>Gets the Keccak hash of <see cref="CanonicalSignature"/>: the topic0 of an event.</summary>
    public Hash256 Hash { get; }

    /// <summary>Gets the 4-byte selector of a function or error.</summary>
    public byte[] Selector { get; }

    /// <summary>Gets the selector as 0x hex.</summary>
    public string SelectorHex => "0x" + Convert.ToHexStringLower(Selector);

    /// <summary>Gets the number of indexed inputs.</summary>
    public int IndexedCount
    {
        get
        {
            int count = 0;
            foreach (McpAbiParam input in Inputs)
            {
                if (input.Indexed) count++;
            }

            return count;
        }
    }

    /// <summary>Gets the canonical output type list, such as <c>(uint112,uint112,uint32)</c>, or <see langword="null"/> if none were declared.</summary>
    public string? OutputTypes
    {
        get
        {
            if (Outputs is null) return null;
            StringBuilder builder = new();
            AppendTypes(builder, Outputs);
            return builder.ToString();
        }
    }

    /// <summary>Parses a signature, throwing on error; meant for built-in constant signatures.</summary>
    /// <exception cref="FormatException">The signature is invalid.</exception>
    public static McpAbiSignature Parse(string text, McpAbiSignatureKind defaultKind = McpAbiSignatureKind.Function) =>
        TryParse(text, defaultKind, out McpAbiSignature? signature, out string? error) ? signature : throw new FormatException(error);

    /// <summary>Parses a human-readable signature.</summary>
    /// <param name="text">The signature text. A leading <c>function</c>, <c>event</c> or <c>error</c> keyword overrides <paramref name="defaultKind"/>.</param>
    /// <param name="defaultKind">The kind assumed when the text has no keyword.</param>
    /// <param name="signature">The parsed signature.</param>
    /// <param name="error">A client-safe description of the problem.</param>
    public static bool TryParse(string? text, McpAbiSignatureKind defaultKind, [NotNullWhen(true)] out McpAbiSignature? signature, [NotNullWhen(false)] out string? error)
    {
        signature = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "the signature is empty; expected something like \"balanceOf(address)\" or \"function transfer(address to, uint256 amount) returns (bool)\"";
            return false;
        }

        if (text.Length > MaxLength)
        {
            error = $"the signature is {text.Length} characters; the maximum is {MaxLength}";
            return false;
        }

        try
        {
            signature = new Parser(text).ParseSignature(defaultKind);
            error = null;
            return true;
        }
        catch (FormatException e)
        {
            error = $"invalid signature: {e.Message}";
            return false;
        }
    }

    /// <summary>Parses a single type such as <c>uint256</c>, <c>address[]</c> or <c>(address,uint256)[2]</c>.</summary>
    public static bool TryParseType(string? text, [NotNullWhen(true)] out McpAbiType? type, [NotNullWhen(false)] out string? error)
    {
        type = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength)
        {
            error = "the type is empty or too long";
            return false;
        }

        try
        {
            Parser parser = new(text);
            type = parser.ParseType(0);
            parser.ExpectEnd();
            error = null;
            return true;
        }
        catch (FormatException e)
        {
            error = $"invalid type: {e.Message}";
            return false;
        }
    }

    /// <summary>Formats the signature as readable text, such as <c>function transfer(address to, uint256 amount) returns (bool)</c>.</summary>
    public override string ToString()
    {
        StringBuilder builder = new(Kind switch
        {
            McpAbiSignatureKind.Event => "event ",
            McpAbiSignatureKind.Error => "error ",
            _ => "function "
        });
        builder.Append(Name);
        AppendParams(builder, Inputs);
        if (Anonymous) builder.Append(" anonymous");
        if (Outputs is not null)
        {
            builder.Append(" returns ");
            AppendParams(builder, Outputs);
        }

        return builder.ToString();
    }

    /// <summary>Formats <paramref name="parameters"/> as a readable list such as <c>(address to, uint256 amount)</c>.</summary>
    public static string FormatParams(IReadOnlyList<McpAbiParam> parameters)
    {
        StringBuilder builder = new();
        AppendParams(builder, parameters);
        return builder.ToString();
    }

    private static void AppendTypes(StringBuilder builder, IReadOnlyList<McpAbiParam> parameters)
    {
        builder.Append('(');
        for (int i = 0; i < parameters.Count; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append(parameters[i].Type.CanonicalName);
        }

        builder.Append(')');
    }

    private static void AppendParams(StringBuilder builder, IReadOnlyList<McpAbiParam> parameters)
    {
        builder.Append('(');
        for (int i = 0; i < parameters.Count; i++)
        {
            if (i > 0) builder.Append(", ");
            builder.Append(parameters[i].Type.CanonicalName);
            if (parameters[i].Indexed) builder.Append(" indexed");
            if (parameters[i].Name.Length > 0) builder.Append(' ').Append(parameters[i].Name);
        }

        builder.Append(')');
    }

    /// <summary>A recursive-descent parser over the Solidity human-readable ABI grammar.</summary>
    private sealed class Parser(string text)
    {
        private const int MaxParameters = 64;
        private readonly string _text = text;
        private int _position;

        public McpAbiSignature ParseSignature(McpAbiSignatureKind kind)
        {
            SkipWhitespace();
            string first = ReadIdentifier("a function name");
            switch (first)
            {
                case "function":
                    kind = McpAbiSignatureKind.Function;
                    first = ReadIdentifier("a function name");
                    break;
                case "event":
                    kind = McpAbiSignatureKind.Event;
                    first = ReadIdentifier("an event name");
                    break;
                case "error":
                    kind = McpAbiSignatureKind.Error;
                    first = ReadIdentifier("an error name");
                    break;
            }

            List<McpAbiParam> inputs = ParseParams(kind == McpAbiSignatureKind.Event, 0);
            List<McpAbiParam>? outputs = null;
            bool anonymous = false;
            while (true)
            {
                SkipWhitespace();
                if (AtEnd)
                {
                    break;
                }

                if (Peek == '(' && outputs is null && kind == McpAbiSignatureKind.Function)
                {
                    outputs = ParseParams(false, 0);
                    continue;
                }

                string word = ReadIdentifier("a modifier or 'returns'");
                switch (word)
                {
                    case "returns" when kind == McpAbiSignatureKind.Function && outputs is null:
                        outputs = ParseParams(false, 0);
                        break;
                    case "anonymous" when kind == McpAbiSignatureKind.Event:
                        anonymous = true;
                        break;
                    case "external" or "public" or "view" or "pure" or "payable" or "nonpayable" or "virtual" or "override"
                        when kind == McpAbiSignatureKind.Function:
                        break;
                    default:
                        throw new FormatException($"unexpected '{word}' at position {_position - word.Length}");
                }
            }

            return new McpAbiSignature(kind, first, inputs, outputs, anonymous);
        }

        public McpAbiType ParseType(int depth)
        {
            if (depth > McpAbiType.MaxNestingDepth)
            {
                throw new FormatException($"types nest deeper than {McpAbiType.MaxNestingDepth} levels");
            }

            SkipWhitespace();
            McpAbiType type;
            if (!AtEnd && Peek == '(')
            {
                type = McpAbiType.TupleOf(ParseParams(false, depth + 1));
            }
            else
            {
                int start = _position;
                string name = ReadIdentifier("a type");
                if (name == "tuple")
                {
                    SkipWhitespace();
                    if (AtEnd || Peek != '(')
                    {
                        throw new FormatException("'tuple' must be followed by its component list, such as tuple(address,uint256)");
                    }

                    type = McpAbiType.TupleOf(ParseParams(false, depth + 1));
                }
                else
                {
                    type = Elementary(name, start);
                }
            }

            while (true)
            {
                SkipWhitespace();
                if (AtEnd || Peek != '[')
                {
                    return type;
                }

                _position++;
                SkipWhitespace();
                int digitsStart = _position;
                while (!AtEnd && char.IsAsciiDigit(Peek)) _position++;
                ReadOnlySpan<char> digits = _text.AsSpan(digitsStart, _position - digitsStart);
                SkipWhitespace();
                Expect(']');
                if (digits.Length == 0)
                {
                    type = McpAbiType.ArrayOf(type);
                }
                else if (digits.Length <= 6 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int length))
                {
                    type = McpAbiType.FixedArrayOf(type, length);
                }
                else
                {
                    throw new FormatException($"array length '{digits}' is too large");
                }
            }
        }

        public void ExpectEnd()
        {
            SkipWhitespace();
            if (!AtEnd)
            {
                throw new FormatException($"unexpected '{Peek}' at position {_position}");
            }
        }

        private List<McpAbiParam> ParseParams(bool allowIndexed, int depth)
        {
            SkipWhitespace();
            Expect('(');
            List<McpAbiParam> parameters = [];
            SkipWhitespace();
            if (!AtEnd && Peek == ')')
            {
                _position++;
                return parameters;
            }

            while (true)
            {
                McpAbiType type = ParseType(depth);
                bool indexed = false;
                string name = string.Empty;
                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd || !IsIdentifierStart(Peek))
                    {
                        break;
                    }

                    string word = ReadIdentifier("a parameter name");
                    switch (word)
                    {
                        case "indexed" when allowIndexed && !indexed && name.Length == 0:
                            indexed = true;
                            break;
                        case "indexed":
                            throw new FormatException(allowIndexed ? "'indexed' must come right after the type" : "'indexed' is only valid in event parameters");
                        case "memory" or "calldata" or "storage" or "payable" when name.Length == 0:
                            break;
                        default:
                            if (name.Length > 0)
                            {
                                throw new FormatException($"unexpected '{word}' after parameter name '{name}'");
                            }

                            name = word;
                            break;
                    }
                }

                parameters.Add(new McpAbiParam(name, type, indexed));
                if (parameters.Count > MaxParameters)
                {
                    throw new FormatException($"more than {MaxParameters} parameters");
                }

                SkipWhitespace();
                if (AtEnd)
                {
                    throw new FormatException("missing ')'");
                }

                char next = _text[_position++];
                if (next == ')')
                {
                    return parameters;
                }

                if (next != ',')
                {
                    throw new FormatException($"expected ',' or ')' at position {_position - 1}, found '{next}'");
                }
            }
        }

        private static McpAbiType Elementary(string name, int position)
        {
            switch (name)
            {
                case "address": return McpAbiType.Address;
                case "bool": return McpAbiType.Bool;
                case "string": return McpAbiType.String;
                case "bytes": return McpAbiType.Bytes;
                case "byte": return McpAbiType.FixedBytes(1);
                case "uint": return McpAbiType.UInt256;
                case "int": return McpAbiType.Int(256);
            }

            if (TrySuffix(name, "uint", out int bits)) return McpAbiType.UInt(bits);
            if (TrySuffix(name, "int", out bits)) return McpAbiType.Int(bits);
            if (TrySuffix(name, "bytes", out int length)) return McpAbiType.FixedBytes(length);
            if (name.StartsWith("fixed", StringComparison.Ordinal) || name.StartsWith("ufixed", StringComparison.Ordinal) || name == "function")
            {
                throw new FormatException($"type '{name}' is not supported");
            }

            throw new FormatException(
                $"unknown type '{name}' at position {position}; use a tuple such as (address,uint256) for structs, uint8 for enums and address for contracts or interfaces");
        }

        private static bool TrySuffix(string name, string prefix, out int value)
        {
            value = 0;
            ReadOnlySpan<char> digits = name.AsSpan(prefix.Length);
            return name.StartsWith(prefix, StringComparison.Ordinal)
                && digits.Length is > 0 and <= 3
                && digits[0] != '0'
                && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        private bool AtEnd => _position >= _text.Length;

        private char Peek => _text[_position];

        private void Expect(char c)
        {
            if (AtEnd || _text[_position] != c)
            {
                throw new FormatException(AtEnd ? $"expected '{c}' at the end" : $"expected '{c}' at position {_position}, found '{_text[_position]}'");
            }

            _position++;
        }

        private string ReadIdentifier(string what)
        {
            SkipWhitespace();
            if (AtEnd || !IsIdentifierStart(Peek))
            {
                throw new FormatException(AtEnd ? $"expected {what} at the end" : $"expected {what} at position {_position}, found '{Peek}'");
            }

            int start = _position;
            while (!AtEnd && (IsIdentifierStart(Peek) || char.IsAsciiDigit(Peek))) _position++;
            return _text[start.._position];
        }

        private void SkipWhitespace()
        {
            while (!AtEnd && char.IsWhiteSpace(Peek)) _position++;
        }

        private static bool IsIdentifierStart(char c) => char.IsAsciiLetter(c) || c is '_' or '$';
    }
}
