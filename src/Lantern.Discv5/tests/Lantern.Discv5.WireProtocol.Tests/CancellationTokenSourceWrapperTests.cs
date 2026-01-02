using Lantern.Discv5.WireProtocol.Utility;
using NUnit.Framework;

namespace Lantern.Discv5.WireProtocol.Tests;

[TestFixture]
public class CancellationTokenSourceWrapperTests
{
    private CancellationTokenSourceWrapper _ctsWrapper;

    [SetUp]
    public void Setup()
    {
        _ctsWrapper = new CancellationTokenSourceWrapper();
    }

    [Test]
    public void GetToken_GetsANonCancelledToken_WhenCalled()
    {
        var token = _ctsWrapper.GetToken();

        Assert.IsFalse(token.IsCancellationRequested);
    }

    [Test]
    public void Cancel_SetsIsCancellationRequestedToTrue_WhenCalled()
    {
        _ctsWrapper.Cancel();

        var token = _ctsWrapper.GetToken();

        Assert.IsTrue(token.IsCancellationRequested);
    }

    [Test]
    public void IsCancellationRequested_ReturnsFalse_WhenNoCancellationRequested()
    {
        Assert.IsFalse(_ctsWrapper.IsCancellationRequested());
    }

    [Test]
    public void IsCancellationRequested_ReturnsTrue_WhenCancellationRequested()
    {
        _ctsWrapper.Cancel();

        Assert.IsTrue(_ctsWrapper.IsCancellationRequested());
    }
}
