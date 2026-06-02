#pragma warning disable IDE0130 // .NET standard 2.0 polyfill — namespace dictated by the runtime
namespace System.Runtime.CompilerServices;
#pragma warning restore IDE0130

internal static class IsExternalInit;

public class RequiredMemberAttribute : Attribute;

#pragma warning disable IDE0290
public class CompilerFeatureRequiredAttribute : Attribute
{
    public CompilerFeatureRequiredAttribute(string name) { }
}
#pragma warning restore IDE0290
