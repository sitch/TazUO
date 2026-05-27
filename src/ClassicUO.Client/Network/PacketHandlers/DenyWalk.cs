using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class DenyWalk
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (world.Player == null)
            return;

        byte seq = p.ReadUInt8();
        ushort x = p.ReadUInt16BE();
        ushort y = p.ReadUInt16BE();
        var direction = (Direction)p.ReadUInt8();
        direction &= Direction.Up;
        sbyte z = p.ReadInt8();

        world.Player.Walker.DenyWalk(seq, x, y, z);
        world.Player.Direction = direction;

        // If the server denied the walk because a door was blocking, try to open it.
        // OnPositionChanged/OnDirectionChanged would not have fired in this case (the
        // player neither moved nor turned), so the bump itself is the only signal we
        // get. includeOpen: true so we also close an open-but-blocking door (some
        // doors on this shard swing into the corridor when opened).
        world.Player.TryOpenDoors(includeOpen: true);

        world.Weather.Reset();
    }
}
