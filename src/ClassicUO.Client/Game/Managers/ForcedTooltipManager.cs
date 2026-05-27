using System.Collections.Generic;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.Game.Managers
{
    internal static class ForcedTooltipManager
    {
        //This class is intended to help generate tooltips for servers before tooltips existed,
        //or for modern-tooltip clients on shards that send rich item properties only via the
        //single-click label channel (gated by the MergeSingleClickIntoTooltip profile flag).

        private static readonly Dictionary<uint, long> _requestedSingleClick = new();
        private const long DELAY = 500;
        private const uint UPDATE_DELAY = 1500;

        private static bool ForceMerge => ProfileManager.CurrentProfile?.MergeSingleClickIntoTooltip == true;

        public static void RequestName(World world, uint serial)
        {
            bool force = ForceMerge;

            if (world.ClientFeatures.TooltipsEnabled && !force) return;

            if (_requestedSingleClick.TryGetValue(serial, out long timeRequested))
                if (Time.Ticks < timeRequested)
                    return;

            // The rev-greater-than-ticks guard was designed for the old-client path where
            // ForcedTooltipManager writes its own revision as `Time.Ticks + UPDATE_DELAY`.
            // Real server OPL revisions (from 0xD6 MegaCliloc) are arbitrary uint values
            // and almost always exceed Time.Ticks, which would suppress every request.
            // The 500ms _requestedSingleClick throttle above already rate-limits this path.
            if (!force && world.OPL.TryGetRevision(serial, out uint revision) && revision > Time.Ticks) return;

            _requestedSingleClick[serial] = Time.Ticks + DELAY;
            GameActions.SingleClick(world, serial);
        }

        public static bool IsObjectTextRequested(World world, Entity parent, string text, ushort hue)
        {
            if (parent == null || (world.ClientFeatures.TooltipsEnabled && !ForceMerge)) return false;

            if (!_requestedSingleClick.ContainsKey(parent.Serial)) return false;

            if (Time.Ticks > _requestedSingleClick[parent.Serial])
            {
                _requestedSingleClick.Remove(parent.Serial);
                return false;
            }

            // Player-directed action feedback ("You are unable to pick this lock",
            // "You must wait to perform another action", "You can't see that") arrives
            // addressed to the targeted item's serial and lands inside the single-click
            // window. Real item properties are noun phrases or "Key: value" lines and
            // never start with "You". Returning false here lets the message flow to the
            // journal normally (no swallow) and skips OPL.AddLabel (no tooltip pollution).
            if (LooksLikePlayerDirected(text)) return false;

            bool hasPrior = world.OPL.TryGetNameAndData(parent.Serial, out string name, out _);

            if (hasPrior && !string.IsNullOrEmpty(name))
            {
                // We have a server-supplied name. The incoming text is either:
                //   1. The bare name (server echoing back our single-click) — ignore.
                //   2. "Name: [Property/Property/...]" — strip the prefix, store as LabelData.
                //   3. Some other annotation — store as-is.
                if (name == text) return true;

                string labelText = text;
                string prefix = name + ": ";
                if (labelText.StartsWith(prefix))
                    labelText = labelText.Substring(prefix.Length).TrimStart();

                // Skill-fail "cannot be picked by normal means" arrives through this same
                // channel because the server addresses it to the chest's serial within
                // the single-click window. It's NOT a property of the chest — record it
                // as a puzzle position (so the in-world glow flips magenta) and drop the
                // text so it never lands in LabelData and pollutes the tooltip.
                if (PickedChestRegistry.IsPuzzleLockFailMessage(labelText))
                {
                    PickedChestRegistry.RecordPuzzle(parent.X, parent.Y, world.MapIndex);
                    return true;
                }

                world.OPL.AddLabel(parent.Serial, labelText);
            }
            else
            {
                // Same skill-fail short-circuit for items without a prior OPL entry —
                // never let the puzzle-lock message become the chest's "Name".
                if (PickedChestRegistry.IsPuzzleLockFailMessage(text))
                {
                    PickedChestRegistry.RecordPuzzle(parent.X, parent.Y, world.MapIndex);
                    return true;
                }
                // No prior OPL entry — use the label text as the Name. Matches the original
                // ForcedTooltipManager behavior for pre-OPL shards.
                world.OPL.Add(parent.Serial, Time.Ticks + UPDATE_DELAY, text, string.Empty, 0);
            }
            return true;
        }

        // Heuristic: action-feedback sentences begin "You ..." (skill fails, cooldowns,
        // range/LOS checks, etc.). Item property labels are noun phrases or Key: value
        // lines and never start with this token.
        private static bool LooksLikePlayerDirected(string text)
        {
            return !string.IsNullOrEmpty(text)
                && text.StartsWith("You ", System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
