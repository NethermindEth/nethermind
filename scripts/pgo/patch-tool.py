"""Adapt the pinned dotnet-pgo converter to managed EventPipe CPU samples."""
from pathlib import Path
import shutil
import sys


def patch(root):
    path = root / "src/coreclr/tools/dotnet-pgo/Program.cs"
    text = path.read_text()

    def replace(before, after):
        nonlocal text
        if text.count(before) != 1:
            raise ValueError(f"Pinned dotnet-pgo source changed: {before[:80]}")
        text = text.replace(before, after)

    # dotnet/runtime#125935: CallWeights array elements must be JSON objects.
    before = '''                            jsonWriter.WriteString("Method", callWeight.Key.ToString());
                            jsonWriter.WriteNumber("Weight", callWeight.Value);'''
    replace(before, '                            jsonWriter.WriteStartObject();\n' + before + '\n                            jsonWriter.WriteEndObject();')
    replace("using Microsoft.Diagnostics.Tracing.Etlx;",
            "using Microsoft.Diagnostics.Tracing.Etlx;\nusing Microsoft.Diagnostics.Tracing.EventPipe;")
    replace("TraceLog.CreateFromEventPipeDataFile(etlFileName, etlxFileName);",
            "TraceLog.CreateFromEventPipeDataFile(etlFileName, etlxFileName, new TraceLogOptions { KeepAllEvents = true });")
    replace("        private int InnerDumpMain()", '''        private bool IsPgoSample(TraceEvent sample) =>
            sample is SampledProfileTraceData ||
            (Get(_command.Spgo) && sample is ClrThreadSampleTraceData { Type: ClrThreadSampleType.Managed });

        private int InnerDumpMain()''')
    replace("foreach (var e in p.EventsInProcess.ByEventType<SampledProfileTraceData>())",
            "foreach (TraceEvent e in p.EventsInProcess.Where(IsPgoSample))")
    replace("(e.TimeStampRelativeMSec < excludeEventsBefore) && (e.TimeStampRelativeMSec > excludeEventsAfter)",
            "(e.TimeStampRelativeMSec < excludeEventsBefore) || (e.TimeStampRelativeMSec > excludeEventsAfter)")
    replace("foreach (SampledProfileTraceData e in p.EventsInProcess.ByEventType<SampledProfileTraceData>())",
            "foreach (TraceEvent e in p.EventsInProcess.Where(IsPgoSample))")
    replace("                            correlator.AttributeSamplesToIP(e.InstructionPointer, 1);", '''                            if (e.TimeStampRelativeMSec < excludeEventsBefore || e.TimeStampRelativeMSec > excludeEventsAfter)
                                continue;
                            ulong ip = e is SampledProfileTraceData kernelSample
                                ? kernelSample.InstructionPointer : e.CallStack()?.CodeAddress.Address ?? 0;
                            if (ip != 0) correlator.AttributeSamplesToIP(ip, 1);''')
    replace("                Dictionary<MethodDesc, MethodChunks> instrumentationDataByMethod", '''                if (callGraph != null)
                {
                    PrintOutput($"Resolved sampled call edges: {callGraph.Values.Sum(edges => edges.Count)}");
                    if (Get(_command.Spgo)) CallChainWriter.Write(Get(_command.OutputFilePath) + ".callchain.json", callGraph);
                }

                Dictionary<MethodDesc, MethodChunks> instrumentationDataByMethod''')
    path.write_text(text)
    shutil.copyfile(Path(__file__).with_name("CallChainWriter.cs"), path.with_name("CallChainWriter.cs"))


if __name__ == "__main__":
    patch(Path(sys.argv[1]))
