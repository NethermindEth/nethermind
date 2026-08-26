// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Evm;
using Nethermind.JsonRpc.Modules.Rpc;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.Serialization.Json;
using Nethermind.Stats.Model;
using Spectre.Console;
using System.Buffers;
using System.Net;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Nethermind.DocGen;

internal static class JsonRpcGenerator
{
    private static readonly string[] _assemblies = [
        "Nethermind.Consensus.Clique",
        "Nethermind.Era1",
        "Nethermind.EraE",
        "Nethermind.Flashbots",
        "Nethermind.HealthChecks",
        "Nethermind.JsonRpc"
    ];
    private const string _objectTypeName = "_object_";
    private static readonly SortedSet<string> _guessedTypeNames = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, string> _knownTypeNames = new()
    {
        [typeof(Address)] = "_string_ (address)",
        [typeof(AddressAsKey)] = "_string_ (address)",
        [typeof(BigInteger)] = "_string_ (decimal integer)",
        [typeof(BlockParameter)] = "_string_ (block number or hash or either of `earliest`, `finalized`, `latest`, `pending`, or `safe`)",
        [typeof(Bloom)] = "_string_ (hex data)",
        [typeof(bool)] = "_boolean_",
        [typeof(byte)] = "_integer_",
        [typeof(byte[])] = "_string_ (hex data)",
        [typeof(byte[][])] = "array of _string_ (hex data)",
        [typeof(Capability)] = "_string_ (protocol/version)",
        [typeof(DateTime)] = "_string_ (date-time)",
        [typeof(DateTimeOffset)] = "_string_ (date-time)",
        [typeof(double)] = "_number_",
        [typeof(double[])] = "array of _number_",
        [typeof(Hash256)] = "_string_ (hash)",
        [typeof(Hash256[])] = "array of _string_ (hash)",
        [typeof(HexBytes)] = "_string_ (hex data)",
        [typeof(int)] = "_integer_",
        [typeof(IPAddress)] = "_string_",
        [typeof(long)] = "_string_ (hex integer)",
        [typeof(PublicKey)] = "_string_ (node id, no \"0x\" prefix)",
        [typeof(Signature)] = "_string_ (hex data)",
        [typeof(string)] = "_string_",
        [typeof(TimeSpan)] = "_string_ (duration)",
        [typeof(TxType)] = "_string_ (transaction type)",
        [typeof(uint)] = "_integer_",
        [typeof(ulong)] = "_string_ (hex integer)",
        [typeof(UInt256)] = "_string_ (hex integer)",
        [typeof(ValueHash256)] = "_string_ (hash)",
    };

    // Labels for members whose own converter writes something other than their type's label
    private static readonly Dictionary<Type, string> _knownConverterTypeNames = new()
    {
        [typeof(BlockNonceConverter)] = "_string_ (8-byte hex data)",
        [typeof(MemoryHexConverter)] = "array of _string_ (32-byte hex data)",
        [typeof(PublicKeyConverter)] = "_string_ (hex data)",
        [typeof(StackHexConverter)] = "array of _string_ (hex integer)",
        [typeof(StorageHexConverter)] = "map of _string_ (32-byte hex data)",
    };

    internal static void Generate(string path)
    {
        path = Path.Join(path, "docs", "interacting", "json-rpc-ns");

        string?[] excluded = new[] {
            typeof(IContextAwareRpcModule).FullName,
            typeof(IEvmRpcModule).FullName,
            typeof(IRpcModule).FullName,
            typeof(IRpcRpcModule).FullName,
            typeof(ISubscribeRpcModule).FullName
        };
        IOrderedEnumerable<Type> types = _assemblies.SelectMany(a => Assembly.Load(a).GetTypes())
            .Where(t => t.IsInterface && typeof(IRpcModule).IsAssignableFrom(t) &&
                !excluded.Any(x => x is not null && (t.FullName?.Contains(x, StringComparison.Ordinal) ?? false)))
            .OrderBy(t => t.Name);

        foreach (string file in Directory.EnumerateFiles(path))
        {
            if (file.EndsWith(".md", StringComparison.Ordinal) &&
                // Skip eth_subscribe.md and eth_unsubscribe.md
                !file.EndsWith("subscribe.md", StringComparison.Ordinal))
            {
                File.Delete(file);
            }
        }

        Dictionary<string, IEnumerable<MethodInfo>> methodMap = [];

        foreach (Type type in types)
        {
            RpcModuleAttribute? attr = type.GetCustomAttribute<RpcModuleAttribute>();

            if (attr is null)
            {
                AnsiConsole.MarkupLine($"[yellow]{type.Name} module type is missing[/]");
                continue;
            }

            string ns = attr.ModuleType.ToLowerInvariant();
            MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public);

            if (!methodMap.TryAdd(ns, methods))
                methodMap[ns] = methodMap[ns].Concat(methods);
        }

        if (methodMap.TryGetValue("eth", out IEnumerable<MethodInfo>? ethMethods))
        {
            // Inject the `subscribe` methods into `eth`
            methodMap["eth"] = ethMethods!
                .Concat(typeof(ISubscribeRpcModule).GetMethods(BindingFlags.Instance | BindingFlags.Public));
        }

        int i = 0;

        foreach ((string ns, IEnumerable<MethodInfo> methods) in methodMap)
        {
            methodMap[ns] = methods.OrderBy(m => m.Name);

            WriteMarkdown(path, ns, methodMap[ns], i++);
        }

        if (_guessedTypeNames.Count != 0)
            AnsiConsole.MarkupLine(
                $"[yellow]Documented from CLR shape, no serializer contract:[/] {string.Join(", ", _guessedTypeNames)}");

        // The probe names number and boolean output before the label is consulted, so such an entry never applies
        foreach ((Type converterType, string label) in _knownConverterTypeNames)
        {
            if (TryProbeScalarKind(converterType, out JsonTokenType token) && token is not JsonTokenType.String)
                AnsiConsole.MarkupLine(
                    $"[yellow]Unreachable converter label, the probe names it {token}:[/] {converterType.Name} = {label}");
        }
    }

    private static void WriteMarkdown(string path, string ns, IEnumerable<MethodInfo> methods, int sidebarIndex)
    {
        string fileName = Path.Join(path, $"{ns}.md");

        using FileStream stream = File.Open(fileName, FileMode.Create);
        using StreamWriter file = new(stream);
        file.NewLine = "\n";

        file.WriteLine($"""
            ---
            title: {ns} namespace
            sidebar_label: {ns}
            sidebar_position: {sidebarIndex}
            ---

            import Tabs from "@theme/Tabs";
            import TabItem from "@theme/TabItem";

            """);

        foreach (MethodInfo method in methods)
        {
            JsonRpcMethodAttribute? attr = method.GetCustomAttribute<JsonRpcMethodAttribute>();

            if (attr is null || !attr.IsImplemented)
                continue;

            if (method.Name.Equals("eth_subscribe", StringComparison.Ordinal) ||
                method.Name.Equals("eth_unsubscribe", StringComparison.Ordinal))
            {
                WriteFromFile(file, Path.Join(path, $"{method.Name}.md"));

                continue;
            }

            file.WriteLine($"""
                ### {method.Name}

                """);

            if (!string.IsNullOrEmpty(attr.Description))
                file.WriteLine($"""
                    {attr.Description}

                    """);

            file.WriteLine("""
                <Tabs>
                """);

            WriteParameters(file, method);
            WriteRequest(file, method);
            WriteResponse(file, method, attr);

            file.WriteLine($"""
                </Tabs>

                """);
        }

        file.Close();

        AnsiConsole.MarkupLine($"[green]Generated[/] {fileName}");
    }

    private static void WriteParameters(StreamWriter file, MethodInfo method)
    {
        ParameterInfo[] parameters = method.GetParameters();

        if (parameters.Length == 0)
            return;

        file.WriteLine($"""
            <TabItem value="params" label="Parameters">

            """);

        int i = 1;

        foreach (ParameterInfo p in parameters)
        {
            JsonRpcParameterAttribute? attr = p.GetCustomAttribute<JsonRpcParameterAttribute>();

            file.Write($"{i++}. `{p.Name}`: ");

            WriteExpandedType(file, p.ParameterType, 2);

            file.WriteLine();
        }

        file.WriteLine("""

            </TabItem>
            """);
    }

    private static void WriteRequest(StreamWriter file, MethodInfo method)
    {
        string parameters = string.Join(", ", method.GetParameters().Select(p => p.Name));

        file.WriteLine($$"""
            <TabItem value="request" label="Request" default>

            ```bash
            curl localhost:8545 \
              -X POST \
              -H "Content-Type: application/json" \
              --data '{
                  "jsonrpc": "2.0",
                  "id": 0,
                  "method": "{{method.Name}}",
                  "params": [{{parameters}}]
                }'
            ```

            </TabItem>
            """);
    }

    private static void WriteResponse(StreamWriter file, MethodInfo method, JsonRpcMethodAttribute attr)
    {
        if (method.ReturnType == typeof(void))
            return;

        file.WriteLine("""
            <TabItem value="response" label="Response">

            """);

        if (!string.IsNullOrEmpty(attr.ResponseDescription))
            file.WriteLine($"""
                {attr.ResponseDescription}

                """);

        file.Write("""
            ```json
            {
              "jsonrpc": "2.0",
              "id": 0,
              "result": result
            }
            ```
            
            `result`: 
            """);

        WriteExpandedType(file, GetReturnType(method.ReturnType));

        if (attr.ResultCanBeNull)
            file.WriteLine("""

                `result` may be `null` in a successful response.
                """);

        file.WriteLine("""
            
            </TabItem>
            """);
    }

    private static void WriteExpandedType(StreamWriter file, Type type, int indentation = 0, bool omitTypeName = false, IEnumerable<string?>? parentTypes = null)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        parentTypes ??= new List<string>();

        if (parentTypes.Any(a => type.FullName?.Equals(a, StringComparison.Ordinal) ?? false))
        {
            file.WriteLine($"{Indent(indentation + 2)}<!--[circular ref]-->");

            return;
        }

        string jsonType = GetJsonTypeName(type);

        if (!jsonType.Equals(_objectTypeName, StringComparison.Ordinal))
        {
            if (TryGetEnumerableItemType(type, out Type? itemType, out bool isDictionary))
            {
                file.Write($"{(isDictionary ? "map" : "array")} of ");

                WriteExpandedType(file, itemType!);
            }
            else
                file.WriteLine(jsonType);

            return;
        }

        if (!omitTypeName)
            file.WriteLine(_objectTypeName);

        if (IsOpaqueJson(type))
            return;

        foreach ((string name, Type memberType, Type? memberConverter) in GetSerializedMembers(type))
        {
            string memberJsonType = GetJsonTypeName(memberType, memberConverter);

            file.WriteLine($"{Indent(indentation + 2)}- `{name}`: {memberJsonType}");

            if (memberJsonType.Equals(_objectTypeName, StringComparison.Ordinal))
                WriteExpandedType(file, memberType, indentation + 2, true, parentTypes.Append(type.FullName));
            else if (memberJsonType.Contains($" of {_objectTypeName}", StringComparison.Ordinal) &&
                TryGetEnumerableItemType(memberType, out Type? itemType, out bool _))
                WriteExpandedType(file, itemType!, indentation + 2, true, parentTypes.Append(type.FullName));
        }
    }

    private static void WriteFromFile(StreamWriter file, string fileName)
    {
        file.Flush();

        using FileStream sourceFile = File.OpenRead(fileName);

        try
        {
            sourceFile.CopyTo(file.BaseStream);
        }
        catch (Exception)
        {
            AnsiConsole.WriteLine($"[red]Failed copying from[/] {fileName}");
        }
    }

    private static string GetJsonTypeName(Type type, Type? converterType = null)
    {
        if (type.IsByRef && type.GetElementType() is { } elementType)
            type = elementType;

        type = Nullable.GetUnderlyingType(type) ?? type;

        // Only explicit member converters: probing a type's default serialization is unsound for
        // value-dependent unions (e.g. `eth_syncing` returns `false` or an object).
        if (converterType is not null && TryProbeScalarKind(converterType, out JsonTokenType token))
        {
            if (token is JsonTokenType.Number)
                return IsFloatingPoint(type) ? "_number_" : "_integer_";
            if (token is JsonTokenType.True or JsonTokenType.False)
                return "_boolean_";
            // a string falls through to keep its flavour from the editorial mapping below
        }

        if (converterType is not null && _knownConverterTypeNames.TryGetValue(converterType, out string? converterName))
            return converterName;

        if (_knownTypeNames.TryGetValue(type, out string? knownName))
            return knownName;

        // An enum serializes as its numeric value unless a converter writes the member name instead
        if (type.IsEnum)
            return type.GetCustomAttribute<JsonConverterAttribute>() is null ? "_integer_" : "_string_";

        if (type.IsGenericType)
        {
            Type definition = type.GetGenericTypeDefinition();

            // Buffer wrappers are written by a converter: as hex when they hold bytes, else as their items
            if (definition == typeof(ArrayPoolList<>) || definition == typeof(CappedArray<>)
                || definition == typeof(Memory<>) || definition == typeof(ReadOnlyMemory<>))
            {
                Type bufferItemType = type.GetGenericArguments()[0];

                return bufferItemType == typeof(byte) ? "_string_ (hex data)" : $"array of {GetJsonTypeName(bufferItemType)}";
            }
        }

        if (TryGetEnumerableItemType(type, out Type? itemType, out bool isDictionary))
            return $"{(isDictionary ? "map" : "array")} of {GetJsonTypeName(itemType!)}";

        return _objectTypeName;
    }

    private static bool IsFloatingPoint(Type type) =>
        type == typeof(double) || type == typeof(float) || type == typeof(decimal);

    // Reads the JSON token the converter actually emits, so number/string/boolean comes from the
    // serializer rather than the CLR type.
    private static bool TryProbeScalarKind(Type converterType, out JsonTokenType token)
    {
        token = JsonTokenType.None;

        // Value type from the converter's own JsonConverter<T> base: nullable converters declare Write
        // against `T?`, and a JsonConverterFactory declares none (null here).
        Type? valueType = ConverterValueType(converterType);

        if (valueType is null)
            return false;

        try
        {
            // Non-null underlying value, else a nullable converter writes null
            object? sample = Activator.CreateInstance(Nullable.GetUnderlyingType(valueType) ?? valueType);

            if (sample is null)
                return false;

            ArrayBufferWriter<byte> buffer = new();

            using (Utf8JsonWriter writer = new(buffer))
                InvokeConverterWrite(converterType, valueType, sample, writer);

            Utf8JsonReader reader = new(buffer.WrittenSpan);
            reader.Read();
            token = reader.TokenType;

            return token is JsonTokenType.Number or JsonTokenType.String or JsonTokenType.True or JsonTokenType.False;
        }
        // Not a documentable scalar (uninstantiable, converter rejects the sample); fall back to the mapping
        catch (Exception)
        {
            return false;
        }
    }

    private static Type? ConverterValueType(Type converterType)
    {
        for (Type? t = converterType; t is not null; t = t.BaseType)
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(JsonConverter<>))
                return t.GetGenericArguments()[0];

        return null;
    }

    private static void InvokeConverterWrite(Type converterType, Type valueType, object sample, Utf8JsonWriter writer)
    {
        JsonConverter converter = (JsonConverter)Activator.CreateInstance(converterType)!;
        MethodInfo write = converterType.GetMethod(
            nameof(JsonConverter<object>.Write),
            [typeof(Utf8JsonWriter), valueType, typeof(JsonSerializerOptions)])!;

        write.Invoke(converter, [writer, sample, EthereumJsonSerializer.JsonOptions]);
    }

    private static Type GetReturnType(Type type)
    {
        Type returnType = type.IsGenericType
            ? type.GetGenericTypeDefinition() == typeof(Task<>)
                ? type.GetGenericArguments()[0].GetGenericArguments()[0]
                : type.GetGenericArguments()[0]
            : type;

        return Nullable.GetUnderlyingType(returnType) ?? returnType;
    }

    private static bool IsOpaqueJson(Type type) =>
        typeof(JsonNode).IsAssignableFrom(type) || type == typeof(JsonElement) || type == typeof(JsonDocument);

    private static JsonTypeInfo? GetContract(Type type)
    {
        try
        {
            return EthereumJsonSerializer.JsonOptions.TryGetTypeInfo(type, out JsonTypeInfo? typeInfo) ? typeInfo : null;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // Types the serializer refuses to model (by-ref, pointer, open generic, colliding member
            // names) throw instead of reporting false; each falls back to its CLR shape
            return null;
        }
    }

    private static IEnumerable<(string Name, Type Type, Type? Converter)> GetSerializedMembers(Type type)
    {
        JsonTypeInfo? contract = GetContract(type);

        if (contract?.Kind is JsonTypeInfoKind.Object)
            return contract.Properties
                .Where(p => p.Get is not null)
                .Select(p => (Name: p.Name, Type: p.PropertyType, Converter: MemberConverter(p.AttributeProvider)))
                .OrderBy(m => m.Name, StringComparer.Ordinal);

        // A hand-rolled converter exposes no contract members, leaving the CLR shape as the only guess
        _guessedTypeNames.Add($"{type.Namespace}.{type.Name}");

        const BindingFlags memberFlags = BindingFlags.Public | BindingFlags.Instance;

        // The serializer sets IncludeFields, so public fields reach the wire alongside properties
        return type.GetProperties(memberFlags).Select(p => (Member: (MemberInfo)p, Type: p.PropertyType))
            .Concat(type.GetFields(memberFlags).Select(f => (Member: (MemberInfo)f, Type: f.FieldType)))
            .Where(m => m.Member.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition is not JsonIgnoreCondition.Always)
            .Select(m => (Name: GetFallbackName(m.Member), Type: m.Type, Converter: MemberConverter(m.Member)))
            .OrderBy(m => m.Name, StringComparer.Ordinal);
    }

    // The member's [JsonConverter], if any: lets two fields of the same type document different wire forms
    private static Type? MemberConverter(ICustomAttributeProvider? member) =>
        member?.GetCustomAttributes(typeof(JsonConverterAttribute), inherit: false) is [JsonConverterAttribute { ConverterType: { } converterType }]
            ? converterType
            : null;

    private static string GetFallbackName(MemberInfo member) =>
        member.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
            ?? JsonNamingPolicy.CamelCase.ConvertName(member.Name);

    private static string Indent(int depth) => string.Empty.PadLeft(depth, ' ');

    private static bool TryGetEnumerableItemType(Type type, out Type? itemType, out bool isDictionary)
    {
        JsonTypeInfo? contract = GetContract(type);

        isDictionary = contract?.Kind is JsonTypeInfoKind.Dictionary;
        itemType = contract?.Kind is JsonTypeInfoKind.Enumerable or JsonTypeInfoKind.Dictionary
            ? contract.ElementType
            : null;

        return itemType is not null;
    }
}
