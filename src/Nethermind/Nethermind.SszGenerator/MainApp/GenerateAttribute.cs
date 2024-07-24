using System;


[AttributeUsage(AttributeTargets.Class)]
sealed class ClassAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Field)]
sealed class FieldAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Method)]
sealed class FunctionAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Struct)]
sealed class FieldStructAttribute : Attribute
{
}
