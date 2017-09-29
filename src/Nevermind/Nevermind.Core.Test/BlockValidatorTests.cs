using Nevermind.Core.Validators;
using NUnit.Framework;

namespace Nevermind.Core.Test
{
    [TestFixture]
    public class BlockValidatorTests
    {
        [Test]
        public void Test()
        {
            BlockValidator blockValidator = new BlockValidator();
            blockValidator.IsValid(Block.Genesis);
        }
    }
}