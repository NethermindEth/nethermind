using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nevermind.Core.Validators;

namespace Nevermind.Core.Test
{
    [TestClass]
    public class BlockValidatorTests
    {
        [TestMethod]
        public void Test()
        {
            BlockValidator blockValidator = new BlockValidator();
            blockValidator.IsValid(Block.Genesis);
        }
    }
}