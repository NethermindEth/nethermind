
using NJsonSchema;
using System.Reflection;
using System.Reflection.Emit;
using Nethermind.Config;
using Nethermind.Core.Collections;

Type iConfigType = typeof(IConfig);

Type[] types = [
    ..Directory.GetFiles(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".", "Nethermind.*.dll").SelectMany(f => GetConfigTypes(Assembly.LoadFrom(f))),
    ];

var assemblyName = new AssemblyName("Nethermind.Config");
var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
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

schema.Properties.Add("$schema", new JsonSchemaProperty { Type = JsonObjectType.String });

Console.WriteLine(schema.ToJson());

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

    // Define the 'set' accessor method
    MethodBuilder setMethodBuilder = typeBuilder.DefineMethod($"set_{propertyName}",
        MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
        null,
        [interfaceType]);

    ILGenerator setIL = setMethodBuilder.GetILGenerator();
    setIL.Emit(OpCodes.Ldarg_0);
    setIL.Emit(OpCodes.Ldarg_1);
    setIL.Emit(OpCodes.Stfld, fieldBuilder);
    setIL.Emit(OpCodes.Ret);

    // Map the get and set methods to the property
    propertyBuilder.SetGetMethod(getMethodBuilder);
    propertyBuilder.SetSetMethod(setMethodBuilder);
}

IEnumerable<Type> GetConfigTypes(Assembly assembly)
{
    return assembly.GetTypes().Where(t => iConfigType.IsAssignableFrom(t) && t.IsClass && !t.IsAbstract);
}
