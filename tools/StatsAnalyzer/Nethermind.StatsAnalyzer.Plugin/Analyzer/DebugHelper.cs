public static class DebugHelper
{
    public static void LogState(string context, params object[] values)
    {
        Console.WriteLine($"[DEBUG] Context: {context}");
        foreach (var value in values)
        {
            if (value == null)
            {
                Console.WriteLine("  - NULL value encountered.");
                continue;
            }

            var type = value.GetType();
            Console.WriteLine($"  - {type.Name}: {value}");

            if (value is Array arr)
            {
                Console.WriteLine($"    Array Length: {arr.Length}");
                Console.WriteLine($"    Elements: [{string.Join(", ", arr)}]");
            }
            else if (value is Dictionary<ulong, ulong> dict)
            {
                Console.WriteLine($"    Dictionary Count: {dict.Count}");
                foreach (var kvp in dict) Console.WriteLine($"      Key: {kvp.Key}, Value: {kvp.Value}");
            }
            else if (value is IEnumerable<ulong> list)
            {
                Console.WriteLine($"    List Elements: [{string.Join(", ", list)}]");
            }
        }

        Console.WriteLine("--------------------------------------------------");
    }
}
