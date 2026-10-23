static class Cases
{
    private static int _passed;
    internal static string Root()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "scripts/config/testnet-matrix.json"))) return directory.FullName;
        throw new DirectoryNotFoundException("Run sync script tests from the repository.");
    }
    internal static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}");
    }
    internal static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    internal static void Test(string name, Action test)
    {
        test();
        Console.WriteLine($"PASS {name}");
        _passed++;
    }
    internal static async Task Test(string name, Func<Task> test)
    {
        await test();
        Console.WriteLine($"PASS {name}");
        _passed++;
    }
    internal static void Finish() => Console.WriteLine($"Passed {_passed} tests.");
}
