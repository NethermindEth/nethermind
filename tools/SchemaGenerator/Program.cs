
using NJsonSchema;
using System.Reflection;
using System.Reflection.Emit;
using Nethermind.Config;
using Nethermind.Core.Collections;

Type iConfigType = typeof(IConfig);

Type[] types = [
    ..Directory.GetFiles(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".", "Nethermind.*.dll").SelectMany(f => GetConfigTypes(Assembly.LoadFrom(f))),
    ];

AssemblyName assemblyName = new("Nethermind.Config");
AssemblyBuilder assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
ModuleBuilder moduleBuilder = assemblyBuilder.DefineDynamicModule("MainModule");

TypeBuilder typeBuilder = moduleBuilder.DefineType("NethermindConfig", TypeAttributes.Public);

foreach (Type configInterface in types)
{
    CreateProperty(typeBuilder, configInterface);
}

Type configType = typeBuilder.CreateType();

JsonSchema schema = JsonSchema.FromType(configType);

foreach (KeyValuePair<string, JsonSchema> def in schema.Definitions)
{
    if (def.Value.Enumeration is { Count: > 0 })
    {
        def.Value.Type = JsonObjectType.String;
        def.Value.Enumeration.Clear();
        def.Value.Enumeration.AddRange(def.Value.EnumerationNames);
    }
}

ApplyConfigParserSchemaOverrides(schema);
PruneUnreferencedDefinitions(schema);

schema.Properties.Add("$schema", new JsonSchemaProperty { Type = JsonObjectType.String });

Console.WriteLine(schema.ToJson());

void ApplyConfigParserSchemaOverrides(JsonSchema schema)
{
    if (!schema.Definitions.TryGetValue(nameof(NetworkNode), out JsonSchema? networkNodeSchema))
    {
        return;
    }

    foreach (JsonSchema definition in schema.Definitions.Values)
    {
        foreach (JsonSchemaProperty property in definition.Properties.Values)
        {
            if (property.Type == JsonObjectType.Array && property.Item?.ActualSchema == networkNodeSchema)
            {
                property.Type = default;
                property.Item = null;
                property.OneOf.Clear();
                property.OneOf.Add(new JsonSchema { Type = JsonObjectType.String });
                property.OneOf.Add(new JsonSchema
                {
                    Type = JsonObjectType.Array,
                    Item = new JsonSchema { Type = JsonObjectType.String }
                });
            }
        }
    }
}

void PruneUnreferencedDefinitions(JsonSchema schema)
{
    HashSet<JsonSchema> visited = [];
    HashSet<JsonSchema> referencedDefinitions = [];

    foreach (JsonSchemaProperty property in schema.Properties.Values)
    {
        Visit(property);
    }

    List<string> unreferencedDefinitionKeys = [];
    foreach (KeyValuePair<string, JsonSchema> definition in schema.Definitions)
    {
        if (!referencedDefinitions.Contains(definition.Value))
        {
            unreferencedDefinitionKeys.Add(definition.Key);
        }
    }

    foreach (string key in unreferencedDefinitionKeys)
    {
        schema.Definitions.Remove(key);
    }

    void Visit(JsonSchema? currentSchema)
    {
        if (currentSchema is null || !visited.Add(currentSchema))
        {
            return;
        }

        if (currentSchema.Reference is not null)
        {
            referencedDefinitions.Add(currentSchema.Reference);
            Visit(currentSchema.Reference);
            return;
        }

        Visit(currentSchema.Item);

        foreach (JsonSchema item in currentSchema.Items)
        {
            Visit(item);
        }

        foreach (JsonSchema item in currentSchema.OneOf)
        {
            Visit(item);
        }

        foreach (JsonSchema item in currentSchema.AnyOf)
        {
            Visit(item);
        }

        foreach (JsonSchema item in currentSchema.AllOf)
        {
            Visit(item);
        }

        foreach (JsonSchemaProperty property in currentSchema.Properties.Values)
        {
            Visit(property);
        }
    }
}

void CreateProperty(TypeBuilder typeBuilder, Type classType)
{
    Type interfaceType = classType.GetInterfaces().First(i => iConfigType.IsAssignableFrom(i) && i != iConfigType);
    string propertyName = interfaceType.Name.Substring(1, interfaceType.Name.Length - 7);
    FieldBuilder fieldBuilder = typeBuilder.DefineField($"_{propertyName}", interfaceType, FieldAttributes.Private);
    PropertyBuilder propertyBuilder = typeBuilder.DefineProperty(propertyName, PropertyAttributes.HasDefault, interfaceType, null);
    MethodBuilder getMethodBuilder = typeBuilder.DefineMethod($"get_{propertyName}",
        MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
        interfaceType,
        Type.EmptyTypes);

    ILGenerator getIL = getMethodBuilder.GetILGenerator();
    getIL.Emit(OpCodes.Ldarg_0);
    getIL.Emit(OpCodes.Ldfld, fieldBuilder);
    getIL.Emit(OpCodes.Ret);

    MethodBuilder setMethodBuilder = typeBuilder.DefineMethod($"set_{propertyName}",
        MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
        null,
        [interfaceType]);

    ILGenerator setIL = setMethodBuilder.GetILGenerator();
    setIL.Emit(OpCodes.Ldarg_0);
    setIL.Emit(OpCodes.Ldarg_1);
    setIL.Emit(OpCodes.Stfld, fieldBuilder);
    setIL.Emit(OpCodes.Ret);

    propertyBuilder.SetGetMethod(getMethodBuilder);
    propertyBuilder.SetSetMethod(setMethodBuilder);
}

IEnumerable<Type> GetConfigTypes(Assembly assembly) =>
    assembly.GetTypes().Where(t => iConfigType.IsAssignableFrom(t) && t.IsClass && !t.IsAbstract);
