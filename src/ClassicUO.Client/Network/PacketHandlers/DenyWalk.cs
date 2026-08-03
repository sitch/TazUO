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

        // Assign the new facing without re-entering the door scan — we call
        // TryOpenDoors explicitly below with the right semantics. Without the
        // suppression bracket, the Direction setter fires OnDirectionChanged
        // (when the facing actually changes) which would do a redundant first
        // scan with includeOpen=false, and the explicit call below could then
        // toggle a different door across the two scans.
        world.Player.BeginSuppressDoorScan();
        try { world.Player.Direction = direction; }
        finally { world.Player.EndSuppressDoorScan(); }

        // Two-phase: try to OPEN a closed blocker first. Only if the half-plane
        // contains no closed candidate at all do we fall back to includeOpen=true
        // to handle the rare swing-into-corridor case. This avoids slamming an
        // open door shut on the player when the actual blocker was a mob, a
        // weight overload, or lag.
        if (!world.Player.TryOpenDoors())
            world.Player.TryOpenDoors(includeOpen: true);

        world.Weather.Reset();
    }
}
