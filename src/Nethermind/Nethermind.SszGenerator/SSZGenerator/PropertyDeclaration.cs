public partial class SszGenerator
{
    class PropertyDeclaration(SszType type, string name)
    {
        public SszType Type { get; } = type;
        public string Name { get; } = name;
    }
}


