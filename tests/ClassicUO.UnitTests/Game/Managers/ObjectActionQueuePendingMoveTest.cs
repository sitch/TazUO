using System.Reflection;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Managers.Structs;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.Managers
{
    // RunUO does not send 0x29 to a 5.0.6.5 client: the item's new place is the only
    // confirmation a queued move gets, and a snapshot that outlived it was put back by
    // the next refused lift (an unequipped axe reappearing in hand).
    public class ObjectActionQueuePendingMoveTest
    {
        private static readonly FieldInfo Pending = typeof(ObjectActionQueue).GetField(
            "_pendingMove", BindingFlags.Instance | BindingFlags.NonPublic);

        private static void SetPending(uint serial) =>
            Pending.SetValue(ObjectActionQueue.Instance, (ItemMoveSource?)new ItemMoveSource { Serial = serial });

        [Fact]
        public void ServerPlacingTheMovedItem_ForgetsThePendingMove()
        {
            SetPending(0x40001234);

            ObjectActionQueue.Instance.OnServerPlaced(0x40001234);

            ObjectActionQueue.Instance.TryGetPendingMove(out _).Should().BeFalse();
        }

        [Fact]
        public void ServerPlacingAnotherItem_KeepsThePendingMove()
        {
            SetPending(0x40001234);

            ObjectActionQueue.Instance.OnServerPlaced(0x40005678);

            ObjectActionQueue.Instance.TryGetPendingMove(out ItemMoveSource src).Should().BeTrue();
            src.Serial.Should().Be(0x40001234u);
            ObjectActionQueue.Instance.ClearPendingMove();
        }
    }
}
