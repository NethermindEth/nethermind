using Lantern.Discv5.WireProtocol.Table;
using NUnit.Framework;

namespace Lantern.Discv5.WireProtocol.Tests;

public class TableOptionsTests
{
    private TableOptions _tableOptions = null!;

    [Test]
    public void Test_TableOptions_CreateDefault()
    {
        _tableOptions = TableOptions.Default;

        Assert.NotNull(_tableOptions);
        Assert.AreEqual(5000, _tableOptions.PingIntervalMilliseconds);
        Assert.AreEqual(300000, _tableOptions.RefreshIntervalMilliseconds);
        Assert.AreEqual(10000, _tableOptions.LookupTimeoutMilliseconds);
        Assert.AreEqual(3, _tableOptions.MaxAllowedFailures);
        Assert.AreEqual(3, _tableOptions.ConcurrencyParameter);
        Assert.AreEqual(2, _tableOptions.LookupParallelism);
        Assert.AreEqual(0, _tableOptions.BootstrapEnrs.Length);
    }

    [Test]
    public void Test_TableOptions_Builder()
    {
        var bootstrapEnrs = new[]
        {
            "enr:-Ku4QImhMc1z8yCiNJ1TyUxdcfNucje3BGwEHzodEZUan8PherEo4sF7pPHPSIB1NNuSg5fZy7qFsjmUKs2ea1Whi0EBh2F0dG5ldHOIAAAAAAAAAACEZXRoMpD1pf1CAAAAAP__________gmlkgnY0gmlwhBLf22SJc2VjcDI1NmsxoQOVphkDqal4QzPMksc5wnpuC3gvSC8AfbFOnZY_On34wIN1ZHCCIyg",
            "enr:-KG4QOtcP9X1FbIMOe17QNMKqDxCpm14jcX5tiOE4_TyMrFqbmhPZHK_ZPG2Gxb1GE2xdtodOfx9-cgvNtxnRyHEmC0ghGV0aDKQ9aX9QgAAAAD__________4JpZIJ2NIJpcIQDE8KdiXNlY3AyNTZrMaEDhpehBDbZjM_L9ek699Y7vhUJ-eAdMyQW_Fil522Y0fODdGNwgiMog3VkcIIjKA"
        };
        var pingIntervalMilliseconds = 1000;
        var refreshIntervalMilliseconds = 20000;
        var lookupTimeoutMilliseconds = 1000;
        var maxAllowedFailures = 5;
        var concurrencyParameter = 5;
        var lookupParallelism = 5;

        _tableOptions = new TableOptions(bootstrapEnrs)
        {
            PingIntervalMilliseconds = pingIntervalMilliseconds,
            RefreshIntervalMilliseconds = refreshIntervalMilliseconds,
            LookupTimeoutMilliseconds = lookupTimeoutMilliseconds,
            MaxAllowedFailures = maxAllowedFailures,
            ConcurrencyParameter = concurrencyParameter,
            LookupParallelism = lookupParallelism
        };

        Assert.NotNull(_tableOptions);
        Assert.AreEqual(pingIntervalMilliseconds, _tableOptions.PingIntervalMilliseconds);
        Assert.AreEqual(refreshIntervalMilliseconds, _tableOptions.RefreshIntervalMilliseconds);
        Assert.AreEqual(lookupTimeoutMilliseconds, _tableOptions.LookupTimeoutMilliseconds);
        Assert.AreEqual(maxAllowedFailures, _tableOptions.MaxAllowedFailures);
        Assert.AreEqual(concurrencyParameter, _tableOptions.ConcurrencyParameter);
        Assert.AreEqual(lookupParallelism, _tableOptions.LookupParallelism);
        Assert.AreEqual(bootstrapEnrs, _tableOptions.BootstrapEnrs.Select(x => x.ToString()).ToArray());
    }
}
