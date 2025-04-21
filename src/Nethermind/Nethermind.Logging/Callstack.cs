using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Nethermind.Logging;

public class StackFrameInfo
{
    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("lineNumber")]
    public int LineNumber { get; set; }

    [JsonPropertyName("structureName")]
    public string StructureName { get; set; } = string.Empty;

    [JsonPropertyName("methodName")]
    public string MethodName { get; set; } = string.Empty;
}

public class StackTraceInfo
{
    [JsonPropertyName("sequence")]
    public List<StackFrameInfo> Sequence { get; set; } = new();

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("traceId")]
    public string TraceId { get; set; } = string.Empty;
}

public static class Callstack
{
    private static readonly HttpClient Client = new();

    public static void LogCallStack(string tag = null)
    {
        var stackFrames = GetCallStack();
        if (!stackFrames.Any())
        {
            return;
        }

        if (!string.IsNullOrEmpty(tag) && stackFrames.Any())
        {
            var frameToTag = stackFrames.Last();
            frameToTag.MethodName = $"{frameToTag.MethodName}+{tag}";
        }

        var stackTraceInfo = new StackTraceInfo
        {
            Sequence = stackFrames,
            Timestamp = DateTime.UtcNow.ToString("o"),
            TraceId = $"trace-{Guid.NewGuid().ToString().Substring(0, 8)}"
        };

        _ = Task.Run(async () =>
        {
            try
            {
                var jsonData = JsonSerializer.Serialize(stackTraceInfo);
                var content = new StringContent(jsonData, Encoding.UTF8, "application/json");
                await Client.PostAsync("http://localhost:3001/api/stackframes", content);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Debug] Failed to post stack trace: {ex.Message}");
            }
        });
    }

    private static bool IsRelevantCall(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        return fileName.Contains("src/Nethermind", StringComparison.OrdinalIgnoreCase) &&
               fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    private static List<StackFrameInfo> GetCallStack()
    {
        var stackFrames = new List<StackFrameInfo>();
        var stackTrace = new StackTrace(true);
        var frames = stackTrace.GetFrames();
        if (frames.Length == 0)
        {
            return stackFrames;
        }

        foreach (var frame in frames)
        {
            var method = frame.GetMethod();
            if (method == null) continue;

            var fileName = frame.GetFileName() ?? "unknown";
            var shortFileName = Path.GetFileName(fileName);
            var lineNumber = frame.GetFileLineNumber();

            if (!IsRelevantCall(fileName)) continue;

            var methodName = method.Name;
            var structureName = method.DeclaringType?.Name ?? "";

            if (structureName.StartsWith("<"))
            {
                structureName = "";
            }

            if (structureName == nameof(Callstack) && methodName is nameof(LogCallStack) or nameof(GetCallStack))
            {
                continue;
            }

            var stackFrameInfo = new StackFrameInfo
            {
                File = shortFileName,
                LineNumber = lineNumber,
                StructureName = structureName,
                MethodName = methodName,
            };

            stackFrames.Add(stackFrameInfo);
        }

        stackFrames.Reverse();
        return stackFrames;
    }
}
