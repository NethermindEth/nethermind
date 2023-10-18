// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Evm;
using Nethermind.JsonRpc.Modules.Rpc;
using Nethermind.JsonRpc.Modules.Witness;
using Newtonsoft.Json;

namespace Nethermind.DocGen;

internal static class JsonRpcGenerator
{
    private const string _objectTypeName = "*object*";

    internal static void Generate()
    {
        var types = new[] { "Nethermind.JsonRpc", "Nethermind.Consensus.Clique" }
            .SelectMany(a => Assembly.Load(a).GetTypes())
            .Where(t =>
                t.IsInterface && t != typeof(IRpcModule) &&
                typeof(IRpcModule).IsAssignableFrom(t) &&
                !typeof(IContextAwareRpcModule).IsAssignableFrom(t) &&
                !t.Name.Equals(nameof(IEvmRpcModule), StringComparison.Ordinal) &&
                !t.Name.Equals(nameof(IRpcRpcModule), StringComparison.Ordinal) &&
                !t.Name.Equals(nameof(IWitnessRpcModule), StringComparison.Ordinal))
            .OrderBy(t => t.Name);

        var i = 0;

        foreach (var type in types)
            WriteMarkdown(type, i++);
    }

    private static void WriteMarkdown(Type rpcType, int sidebarIndex)
    {
        var rpcName = rpcType.Name[1..].Replace("RpcModule", null).ToLowerInvariant();

        using var file = new StreamWriter(File.OpenWrite($"{rpcName}.md"));
        file.NewLine = "\n";

        file.WriteLine($"""
            ---
            title: {rpcName} namespace
            sidebar_label: {rpcName}
            sidebar_position: {sidebarIndex}
            ---

            import Tabs from "@theme/Tabs";
            import TabItem from "@theme/TabItem";

            """);

        var methods = rpcType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .OrderBy(m => m.Name);

        foreach (var method in methods)
        {
            var attr = method.GetCustomAttribute<JsonRpcMethodAttribute>();

            if (attr is null || !attr.IsImplemented)
                continue;

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
    }

    private static void WriteParameters(StreamWriter file, MethodInfo method)
    {
        var parameters = method.GetParameters();

        if (parameters.Length == 0)
            return;

        file.WriteLine($"""
            <TabItem value="params" label="Parameters">

            """);

        var i = 1;

        foreach (var p in parameters)
        {
            var attr = p.GetCustomAttribute<JsonRpcParameterAttribute>();

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
        var parameters = string.Join(", ", method.GetParameters().Select(p => p.Name));

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

        file.WriteLine("""
            
            </TabItem>
            """);
    }

    private static void WriteExpandedType(StreamWriter file, Type type, int indentation = 0, bool omitTypeName = false, IEnumerable<string?>? parentTypes = null)
    {
        parentTypes ??= new List<string>();

        if (parentTypes.Any(a => type.FullName?.Equals(a, StringComparison.Ordinal) ?? false))
        {
            file.WriteLine($"{Indent(indentation + 2)}<!--[circular ref]-->");

            return;
        }

        var jsonType = GetJsonTypeName(type);

        if (!jsonType.Equals(_objectTypeName, StringComparison.Ordinal))
        {
            if (TryGetEnumerableItemType(type, out var itemType, out var isDictionary))
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

        var properties = GetSerializableProperties(type);

        foreach (var prop in properties)
        {
            var propJsonType = GetJsonTypeName(prop.PropertyType);

            file.WriteLine($"{Indent(indentation + 2)}- `{GetSerializedName(prop)}`: {propJsonType}");

            if (propJsonType.Equals(_objectTypeName, StringComparison.Ordinal))
                WriteExpandedType(file, prop.PropertyType, indentation + 2, true, parentTypes.Append(type.FullName));
            else if (propJsonType.Contains($" of {_objectTypeName}", StringComparison.Ordinal) &&
                TryGetEnumerableItemType(prop.PropertyType, out var itemType, out var _))
                WriteExpandedType(file, itemType!, indentation + 2, true, parentTypes.Append(type.FullName));
        }
    }

    private static string GetJsonTypeName(Type type)
    {
        var underlyingType = Nullable.GetUnderlyingType(type);

        if (underlyingType is not null)
            return GetJsonTypeName(underlyingType);

        if (type.IsEnum)
            return "*integer*";

        if (TryGetEnumerableItemType(type, out var itemType, out var isDictionary))
            return $"{(isDictionary ? "map" : "array")} of {GetJsonTypeName(itemType!)}";

        return type.Name switch
        {
            "Address" => "*string* (address)",
            "BigInteger"
                or "Int32"
                or "Int64"
                or "Int64&"
                or "UInt64"
                or "UInt256" => "*string* (hex integer)",
            "BlockParameter" => "*string* (block number or hash or either of `earliest`, `finalized`, `latest`, `pending`, or `safe`)",
            "Bloom"
                or "Byte"
                or "Byte[]" => "*string* (hex data)",
            "Boolean" => "*boolean*",
            "Keccak" => "*string* (hash)",
            "String" => "*string*",
            "TxType" => "*string* (transaction type)",
            _ => _objectTypeName
        };
    }

    private static Type GetReturnType(Type type)
    {
        var returnType = type.GetGenericTypeDefinition() == typeof(Task<>)
            ? type.GetGenericArguments()[0].GetGenericArguments()[0]
            : type.GetGenericArguments()[0];

        return Nullable.GetUnderlyingType(returnType) ?? returnType;
    }

    private static IEnumerable<PropertyInfo> GetSerializableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .OrderBy(p => p.Name);

    private static string GetSerializedName(PropertyInfo prop) =>
        prop.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName
            ?? $"{prop.Name[0].ToString().ToLowerInvariant()}{prop.Name[1..]}"; // Ugly incomplete camel case

    private static string Indent(int depth) => string.Empty.PadLeft(depth, ' ');

    private static bool TryGetEnumerableItemType(Type type, out Type? itemType, out bool isDictionary)
    {
        if (type.IsArray && type.HasElementType)
        {
            var elementType = type.GetElementType();

            // Ignore a byte array as it is treated as a hex string
            if (elementType == typeof(byte))
            {
                itemType = null;
                isDictionary = false;

                return false;
            }

            itemType = type.GetElementType();
            isDictionary = false;

            return true;
        }

        if (type.IsInterface && type.IsGenericType)
        {
            if (type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                itemType = type.GetGenericArguments().Last();
                isDictionary = false;

                return true;
            }

            if (type.GetGenericTypeDefinition() == typeof(IDictionary<,>))
            {
                itemType = type.GetGenericArguments().Last();
                isDictionary = true;

                return true;
            }
        }

        if (type.IsGenericType && type.GetInterfaces()
            .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
        {
            itemType = type.GetGenericArguments().Last();
            isDictionary = type.GetInterfaces()
                .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));

            return true;
        }

        itemType = null;
        isDictionary = false;

        return false;
    }
}
