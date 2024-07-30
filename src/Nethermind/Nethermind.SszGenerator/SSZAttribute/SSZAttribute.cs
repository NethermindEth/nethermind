using System;

namespace SSZAttribute
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class SSZClassAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SSZFieldAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class SSZFunctionAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Struct)]
    public sealed class SSZStructAttribute : Attribute
    {
    }
}
