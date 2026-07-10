public static class Check
{
    public static void ThrowIfNull<T>(Nullable<T> value, string name) where T : struct
    {
        if (!value.HasValue)
            throw new ArgumentNullException(name);
    }

    public static void ThrowIfNull<T>(T? value, string name) where T : class
    {
        if (value == null)
            throw new ArgumentNullException(name);
    }
}
