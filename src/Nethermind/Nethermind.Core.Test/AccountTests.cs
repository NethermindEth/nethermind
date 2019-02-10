using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class AccountTests
    {
        [Test]
        public void Test_totally_empty()
        {
            Account account = Account.TotallyEmpty;
            Assert.True(account.IsTotallyEmpty, "totally empty");
            Assert.True(account.IsEmpty, "empty");
        }

        [Test]
        public void Test_just_empty()
        {
            Account account = Account.TotallyEmpty;
            account = account.WithChangedStorageRoot(TestItem.KeccakA);
            Assert.False(account.IsTotallyEmpty, "totally empty");
            Assert.True(account.IsEmpty, "empty");
        }
    }
}