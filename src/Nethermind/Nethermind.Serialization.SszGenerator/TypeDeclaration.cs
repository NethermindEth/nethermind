public partial class SszGenerator
{
    class TypeDeclaration(string? typeNamespaceName, string typeName, bool isStruct, PropertyDeclaration[] members)
    {
        public string? TypeNamespaceName { get; } = typeNamespaceName;
        public string TypeName { get; } = typeName;
        public bool IsStruct { get; } = isStruct;
        public PropertyDeclaration[] Members { get; } = members;
    }
}


