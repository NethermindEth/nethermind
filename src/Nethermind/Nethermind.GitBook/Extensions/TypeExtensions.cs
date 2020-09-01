using System;

namespace Nethermind.GitBook.Extensions
{
    public static class TypeExtensions
    {
        public static bool IsNullable(this Type type)
        {
            return Nullable.GetUnderlyingType(type) != null;
        }
    }
}