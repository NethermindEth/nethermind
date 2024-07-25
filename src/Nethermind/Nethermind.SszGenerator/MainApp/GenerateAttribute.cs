using System;


[AttributeUsage(AttributeTargets.Class)]
sealed class SSZClassAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Field)]
sealed class SSZFieldAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Method)]
sealed class SSZFunctionAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Struct)]
sealed class SSZStructAttribute : Attribute
{
}
