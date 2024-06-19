

namespace Nethermind.Db.Test
{

    public class LogIndexTests
    {

        [Test]
        public void Can_get_all_on_empty()
        {
            _ = _db.GetAll().ToList();
        }

    }

}
