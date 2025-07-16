using FluentAssertions;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Nethermind.Tools.Kute.JsonRpcMethodFilter;
using NUnit.Framework;

namespace Nethermind.Tools.Kute.Test;

public class JsonRpcFilterTests
{
    /*
    ### Use all messages in the folder `/rpc-logs`

    ```
    -i /rpc-logs -s keystore/jwt-secret
    ```

    ### Use a single messages file and emit results as JSON

    ```
    -i /rpc.0 -s keystore/jwt-secret -o Json
    ```

    ### Use a single messages file and record all responses into a new file

    ```
    -i /rpc.0 -s keystore/jwt-secret -r rpc.responses.txt
    ```

    ### Use a single message file, using only `engine` and `eth` methods

    ```
    -i /rpc.0 -s keystore/jwt-secret -f engine, eth
    ```

    ### Connect to a Nethermind Client running in a specific address and TTL

    ```
    -i /rpc.0 -s keystore/jwt-secret -a http://192.168.1.100:8551 --ttl 30
    ```

    ### Run in "dry" mode (no communication with the Nethermind Client)

    ```
    -i /rpc.0 -s keystore/jwt-secret -d
    ```
    */

    [SetUp]
    public void Setup()
    {
    }

    private static IEnumerable<TestCaseData> UnlimitedMethodTestCases()
    {
        yield return new TestCaseData(
            new List<string> { "engine_newPayloadV2" },
            new List<string> { "engine_newPayloadV2" }
        ).SetName("Exact method match");

        yield return new TestCaseData(
            new List<string> { "engine_newPayloadV[23]" },
            new List<string> { "engine_newPayloadV2", "engine_newPayloadV3" }
        ).SetName("Multiple methods match with regex");

        yield return new TestCaseData(
            new List<string> { "eth_*" },
            new List<string> { "eth_getBlockByNumber", "eth_sendTransaction" }
        ).SetName("Wildcard match for eth methods");

        yield return new TestCaseData(
            new List<string> { "engine_*", "eth_*" },
            new List<string> { "engine_newPayloadV2", "eth_getBlockByNumber", "eth_sendTransaction" }
        ).SetName("Multiple patterns match");
    }

    [TestCaseSource(nameof(UnlimitedMethodTestCases))]
    public void AcceptsUnlimitedMethods(List<string> patterns, List<string> methodsNames)
    {
        var filter = new ComposedJsonRpcMethodFilter(
            patterns
                .Select(pattern => new PatternJsonRpcMethodFilter(pattern) as IJsonRpcMethodFilter)
                .ToList());

        foreach (string methodName in methodsNames)
        {
            filter.ShouldSubmit(methodName).Should().BeTrue();
        }
    }

    private static IEnumerable<TestCaseData> UnlimitedMethodNegativeTestCases()
    {
        yield return new TestCaseData(
            new List<string> { "engine_newPayloadV2" },
            new List<string> { "eth_getBlockByNumber", "eth_sendTransaction" }
        ).SetName("Non-matching method");

        yield return new TestCaseData(
            new List<string> { "engine_newPayloadV[23]" },
            new List<string> { "engine_newPayloadV1", "engine_newPayloadV4" }
        ).SetName("Non-matching regex method");

        yield return new TestCaseData(
            new List<string> { "eth_*" },
            new List<string> { "engine_newPayloadV2", "engine_sendTransaction" }
        ).SetName("Wildcard non-matching method");

        yield return new TestCaseData(
            new List<string> { "engine_*", "eth_*" },
            new List<string> { "net_version", "web3_clientVersion" }
        ).SetName("Multiple patterns with no matches");
    }

    [TestCaseSource(nameof(UnlimitedMethodNegativeTestCases))]
    public void RejectsUnlimitedMethods(List<string> patterns, List<string> methodsNames)
    {
        var filter = new ComposedJsonRpcMethodFilter(
            patterns
                .Select(pattern => new PatternJsonRpcMethodFilter(pattern) as IJsonRpcMethodFilter)
                .ToList());

        foreach (string methodName in methodsNames)
        {
            filter.ShouldSubmit(methodName).Should().BeFalse();
        }
    }

    private static IEnumerable<TestCaseData> LimitedMethodTestCases()
    {
        yield return new TestCaseData(
            new List<string> { "engine_newPayloadV2=1" },
            "engine_newPayloadV2",
            1
        ).SetName("Exact method with count");

        yield return new TestCaseData(
            new List<string> { "engine_newPayloadV[23]=2" },
            "engine_newPayloadV2",
            2
        ).SetName("Regex method with count");

        yield return new TestCaseData(
            new List<string> { ".*=3" },
            "web3_clientVersion",
            3
        ).SetName("Any method with count");

        yield return new TestCaseData(
            new List<string> { "engine_*=5", "eth_*=5" },
            "engine_newPayloadV2",
            5
        ).SetName("Multiple patterns with counts");
    }

    [TestCaseSource(nameof(LimitedMethodTestCases))]
    public void AcceptsLimitedMethods(List<string> patterns, string methodName, int count)
    {
        var filter = new ComposedJsonRpcMethodFilter(
            patterns
                .Select(pattern => new PatternJsonRpcMethodFilter(pattern) as IJsonRpcMethodFilter)
                .ToList());

        for (int i = 0; i < count; i++)
        {
            filter.ShouldSubmit(methodName).Should().BeTrue();
        }
    }
}
