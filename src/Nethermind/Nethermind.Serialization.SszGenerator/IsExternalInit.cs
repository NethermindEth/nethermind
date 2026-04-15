namespace System.Runtime.CompilerServices;

// .NET standard 2.0 needs these types to allow modern C# features

internal static class IsExternalInit;

public class RequiredMemberAttribute : Attribute;

#pragma warning disable IDE0290
public class CompilerFeatureRequiredAttribute : Attribute
{
    public CompilerFeatureRequiredAttribute(string name) { }
}
#pragma warning restore IDE0290
