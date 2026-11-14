using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers.SpellVisualRange;
using ClassicUO.Game.Managers.Structs;
using ClassicUO.Utility.Logging;
using Lock = System.Threading.Lock;

namespace ClassicUO.Game.Managers;

public sealed partial class AutoUnequipActionManager : IDisposable
{
    private struct Armament(uint serial, Layer layer)
    {
        public readonly uint Serial = serial;
        public readonly Layer Layer = layer;
    }

    public static AutoUnequipActionManager Instance { get; private set; }

    private readonly World _world;
    private readonly CancellationTokenSource _cTokenSource = new();
    private readonly Task _interceptConsumerCompletion;

    /// <summary>
    ///     An intercepted action. A spell holds the armaments off until its cast is over; a potion does not.
    /// </summary>
    private readonly record struct Intercept(Action Run, bool IsSpell);

    private readonly Channel<Intercept> _flushChannel = Channel.CreateUnbounded<Intercept>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false }
    );

    #region Re-arm hold

    // Equipping anything while a spell is being cast disturbs it on RunUO-style servers
    // (Spell.OnCasterEquiping), so the re-arm cannot be queued straight behind the cast: it has to
    // wait until the cast is over. "Over" is the spell's target cursor having opened and closed, a
    // failure message, or, for a spell with no cursor, the timeout below.

    /// <summary>How often the consumer looks at a cast it is holding the armaments for (ms)</summary>
    private const int REARM_POLL_MS = 50;

    /// <summary>
    ///     How long after the cast packet a spell with no target cursor is taken to be over (ms).
    ///     Longer than the slowest cast (8th circle) plus a round trip; shorter only re-arms into a cast.
    /// </summary>
    private const uint NO_CURSOR_TIMEOUT_MS = 4000;

    /// <summary>How long a queued cast may sit unsent before the hold gives up on it (ms)</summary>
    private const uint UNSENT_CAST_TIMEOUT_MS = 15000;

    /// <summary>Armaments taken off and not yet put back. Consumer task only.</summary>
    private readonly List<Armament> _held = new();

    /// <summary>The queued re-arm actions, so a new cast can cancel them. Consumer task only.</summary>
    private readonly List<ObjectActionQueueItem> _rearmItems = new();

    /// <summary>How many of <see cref="_rearmItems"/> have yet to run; a fresh array per re-arm</summary>
    private int[] _rearmsLeft;

    /// <summary>Whether the held armaments are waiting on a spell (as opposed to a potion)</summary>
    private bool _awaitingCast;

    /// <summary>
    ///     True while armaments are off and not yet back. A cast made now is intercepted even with
    ///     empty hands, so it can cancel a queued re-arm instead of being disturbed by it.
    /// </summary>
    private volatile bool _holdingArms;

    private int _castsQueued;      // casts handed to the action queue and not yet sent
    private uint _holdStartTick;   // when the last batch was queued
    private uint _castSentTick;    // main thread: when the last held cast went out
    private uint _cursorsAtCast;   // main thread: TargetManager.ServerCursorCount at that moment

    #endregion

    #region Dispose

    private readonly Lock _disposalLock = new();
    private bool _disposed;

    #endregion

    #region Accessors

    /// <summary>
    ///     Checks whether the manager is in a valid state and can intercept calls.
    ///     Note that this does not consider the per-action profile toggles.
    /// </summary>
    /// <returns>True if the manager is ready to intercept, false otherwise</returns>
    private bool CanIntercept => !_disposed && IsPlayerBackpackAvailable;

    /// <summary>
    ///     Whether spell casts may be intercepted, per the profile's settings and the manager's state
    /// </summary>
    private bool IsSpellCastInterceptionEnabled => CanIntercept && ProfileManager.CurrentProfile?.AutoUnequipForSpellCasting == true;

    /// <summary>
    ///     Whether potion uses may be intercepted, per the profile's settings and the manager's state
    /// </summary>
    private bool IsPotionInterceptionEnabled => CanIntercept && ProfileManager.CurrentProfile?.AutoUnequipForPotions == true;

    /// <summary>
    ///     Determines whether the player's backpack is available
    /// </summary>
    private bool IsPlayerBackpackAvailable => _world?.Player?.Backpack != null;

    #endregion

    public AutoUnequipActionManager(World world)
    {
        _world = world;
        Instance = this;
        _interceptConsumerCompletion = Task.Run(ActionConsumer).ContinueWith(result =>
        {
            if (result.IsCanceled || result.Exception == null)
                return;

            Log.Warn("Auto-Unequip manager task processor faulted:");
            Log.Warn(result.Exception.ToString());
            // We could restart but honestly if it faulted, we're probably better off leaving it disabled.
            MainThreadQueue.InvokeOnMainThread(() =>
            {
                if (_world == null)
                    return;
                try
                {
                    GameActions.Print(_world, TazLang.Get("auto_unequip_failed_stopped"), Constants.HUE_ERROR);
                }
                catch (Exception e)
                {
                    Log.Error($"Failed to notify user of auto-equip agent failure: {e}");
                }
            });
        });
    }

    #region Public Methods

    /// <summary>
    ///     Attempts to intercept a spell cast
    /// </summary>
    /// <param name="spellIndex">The spell being intercepted</param>
    /// <returns>True if the spell was intercepted, false otherwise</returns>
    public bool TryInterceptSpellCast(int spellIndex) =>
        ShouldInterceptCast(spellIndex) && _flushChannel.Writer.TryWrite(new Intercept(() => GameActions.CastSpellDirect(spellIndex), true));

    /// <summary>
    ///     Attempts to intercept a double click
    /// </summary>
    /// <param name="itemSerial">The serial of the item being double-clicked</param>
    /// <param name="sendDoubleClickDelegate">The original 'send double click' function</param>
    /// <returns>True if the click was intercepted, false otherwise</returns>
    public bool TryInterceptDoubleClick(uint itemSerial, Action<uint> sendDoubleClickDelegate) =>
        ShouldInterceptDblClick(itemSerial, sendDoubleClickDelegate) && _flushChannel.Writer.TryWrite(new Intercept(() => sendDoubleClickDelegate(itemSerial), false));

    /// <summary>
    ///     Disposes the manager instance.
    ///     Note this method may take a few milliseconds to return.
    /// </summary>
    public void Dispose()
    {
        lock (_disposalLock)
        {
            if (_disposed)
                return;

            // First, issue a cancel. The token propagates to the wait for the main thread as well
            _cTokenSource.Cancel();

            // Then, close the producers
            _flushChannel.Writer.Complete();

            // Wait for the consumer to return
            Task.WaitAll(_interceptConsumerCompletion);
            // Finally, dispose of the rest
            _cTokenSource.Dispose();
            Instance = null;

            _disposed = true;
        }
    }

    #endregion

    #region PrivateMethods

    /// <summary>
    ///     Determines whether a spell cast should be intercepted
    /// </summary>
    /// <param name="spellIndex">The intercepted spell's index</param>
    /// <returns>True if the spell should be intercepted, false otherwise</returns>
    private bool ShouldInterceptCast(int spellIndex)
    {
        if (!IsSpellCastInterceptionEnabled)
            return false;

        if (spellIndex is >= 100 and <= 678 or >= 700)
            return false;

        // Armaments are already off for an earlier cast: this one must go through the consumer too,
        // or a re-arm queued for the earlier cast lands in the middle of it.
        if (_holdingArms)
            return true;

        List<Armament> arms = GetArmingState();
        if (arms.Count <= 0)
            return false;

        foreach (Armament arm in arms)
        {
            if (!_world.OPL.TryGetNameAndData(arm.Serial, out string _, out string data))
                return true; // If missing from OPL, err on the side of caution and intercept

            if (string.IsNullOrWhiteSpace(data) || !IsSpellChannelling().IsMatch(data))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Determines whether a Double-Click should be intercepted
    /// </summary>
    /// <param name="serial">The clicked target's serial</param>
    /// <param name="sendDoubleClickDelegate">The original 'send double click' function</param>
    /// <returns>True if the event should be intercepted, false otherwise</returns>
    private bool ShouldInterceptDblClick(uint serial, Action<uint> sendDoubleClickDelegate)
    {
        if (sendDoubleClickDelegate == null || !IsPotionInterceptionEnabled)
            return false;

        return IsDrinkablePotionItem(serial) && GetArmingState().Count > 0;
    }

    /// <summary>
    ///     Gets a snapshot of the player's current arming state, that is, what weapons/shields they have equipped
    /// </summary>
    /// <returns>A list of equipped weapons/shields</returns>
    private List<Armament> GetArmingState()
    {
        // Check if player has weapons equipped
        Item oneHanded = _world.Player.FindItemByLayer(Layer.OneHanded);
        Item twoHanded = _world.Player.FindItemByLayer(Layer.TwoHanded);

        var arms = new List<Armament>();
        if (oneHanded?.Serial != null)
            arms.Add(new Armament(oneHanded.Serial, Layer.OneHanded));

        if (twoHanded?.Serial != null)
            arms.Add(new Armament(twoHanded.Serial, Layer.TwoHanded));

        return arms;
    }

    /// <summary>
    ///     Heuristically determines whether an item, given by serial, is a player-drinkable potion
    /// </summary>
    /// <param name="serial">The item's serial</param>
    /// <returns></returns>
    private bool IsDrinkablePotionItem(uint serial)
    {
        Item item = _world.Items.Get(serial);
        if (item == null)
            return false;

        // Check if item is 0xF06-0xF09 OR 0xF0B-0xF0C - these are the 'drinkable' potion graphics
        if (item.Graphic is (< 0xF06 or > 0xF09) and (< 0xF0B or > 0xF0C))
            return false;

        // Get the un-localized item name. We perform an extra check here as graphics may be shared by unrelated items
        if (_world.OPL.TryGetNameAndData(item.Serial, out string name, out _))
            // Use a simple regular expression to heuristically determine if something is a potion.
            // To be expanded on with configuration.
            return name != null && IsPotionRegex().IsMatch(name);

        // Data doesn't exist in OPL cache - stay conservative and assume this is *not* a potion
        return false;
    }

    /// <summary>
    ///     The task channel's consumer, responsible for actually performing any disarming/rearming
    /// </summary>
    private async Task ActionConsumer()
    {
        try
        {
            while (true)
            {
                if (_held.Count == 0)
                {
                    // Wait for interception requests/cancellation
                    if (!await _flushChannel.Reader.WaitToReadAsync(_cTokenSource.Token))
                        break;

                    // A micro-delay to let producers settle, in case of a series of actions
                    await Task.Delay(50, _cTokenSource.Token);
                }
                else
                    // Armaments are off: watch the cast they are off for, and for a new one
                    await Task.Delay(REARM_POLL_MS, _cTokenSource.Token);

                // Gather-up whatever tasks we've collected until now
                var tasks = new List<Intercept>();
                while (_flushChannel.Reader.TryRead(out Intercept task))
                    tasks.Add(task);

                if (tasks.Count > 0)
                    ExecuteBatchedTasks(tasks);
                else if (_held.Count > 0)
                    ReEquipWhenCastIsOver();
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info("Auto-Unequip action consumer has been interrupted by a cancellation request");
        }
    }

    /// <summary>
    ///     Executes the given tasks, after ensuring player has been disarmed.
    ///     Re-arms the player afterward: at once for a potion, once the cast is over for a spell.
    /// </summary>
    /// <param name="tasks">The tasks to execute. These could be "cast a spell" or "drink a potion"</param>
    private void ExecuteBatchedTasks(List<Intercept> tasks)
    {
        if (_disposed)
            return;

        // Dispatch to MT to avoid any potential sync issues.
        //
        // We need to fetch a 'fresh' state - no point issuing an unequip command if we're not currently armed.
        //
        // Note that there's a slight edge case here - if the equipped armaments were changed
        // after enqueuing a task but before we got here, there may be a change in the 'spell channeling' status.
        //
        // Theoretically, then, a disarm may no longer be necessary.
        // This is, however, a minor and mostly harmless quirk.
        List<Armament> arms = MainThreadQueue.BubblingInvokeOnMainThread(() => IsPlayerBackpackAvailable ? GetArmingState() : null, _cTokenSource.Token);
        if (arms == null) // null means world/player/backpack are gone and we should stop.
        {
            ReleaseHold();
            return;
        }

        _cTokenSource.Token.ThrowIfCancellationRequested();

        // A re-arm still queued for an earlier cast would run before these and disturb them
        CancelQueuedReEquip();

        foreach (Armament arm in arms)
            if (!_held.Exists(h => h.Serial == arm.Serial))
                _held.Add(arm);

        // Potions included: their re-arm is queued behind them, and a cast made before it runs must cancel it
        _holdingArms = _held.Count > 0;

        // Enqueue a disarm if necessary
        if (arms.Count > 0)
            EnqueueUnequip(arms);
        _cTokenSource.Token.ThrowIfCancellationRequested();

        // Enqueue all batched actions
        foreach (Intercept task in tasks)
        {
            if (task.IsSpell && _held.Count > 0)
            {
                _awaitingCast = true;
                Interlocked.Increment(ref _castsQueued);
                ObjectActionQueue.Instance.Enqueue(new ObjectActionQueueItem(() =>
                {
                    task.Run();
                    OnHeldCastSent();
                }), ActionPriority.EquipItem);
            }
            else
                ObjectActionQueue.Instance.Enqueue(new ObjectActionQueueItem(task.Run), ActionPriority.EquipItem);
        }

        _holdStartTick = Time.Ticks;
        _cTokenSource.Token.ThrowIfCancellationRequested();

        // Nothing to wait for (potions only): re-arm straight behind the actions
        if (_held.Count > 0 && !_awaitingCast)
            EnqueueReEquip(_held);
    }

    /// <summary>
    ///     Main thread, right after a held cast's packet went out: the cast is over when a target
    ///     cursor newer than this has closed, or a failure newer than this was reported.
    /// </summary>
    private void OnHeldCastSent()
    {
        _castSentTick = Time.Ticks;
        _cursorsAtCast = _world?.TargetManager?.ServerCursorCount ?? 0;

        if (Interlocked.Decrement(ref _castsQueued) < 0)
            Interlocked.Exchange(ref _castsQueued, 0);
    }

    /// <summary>
    ///     Main thread. Whether the cast the armaments are off for is over, so equipping can no longer disturb it.
    /// </summary>
    /// <returns>True when over, false while casting or targeting, null when the player is gone</returns>
    private bool? IsHeldCastOver()
    {
        if (!IsPlayerBackpackAvailable)
            return null;

        if (Volatile.Read(ref _castsQueued) > 0)
        {
            // Still waiting its turn in the action queue. If the queue was cleared it never will run.
            if (Time.Ticks - _holdStartTick < UNSENT_CAST_TIMEOUT_MS)
                return false;

            Interlocked.Exchange(ref _castsQueued, 0);
            return true;
        }

        // No mana, no reagents, disturbed, fizzled...
        if (SpellVisualRangeManager.Instance.LastCastFailedTick > _castSentTick)
            return true;

        // The cursor is counted rather than watched, because an auto-target can answer it within the
        // packet handler and it is never seen open.
        TargetManager targets = _world.TargetManager;
        if (targets.ServerCursorCount != _cursorsAtCast)
            return !targets.IsTargeting;

        return Time.Ticks - _castSentTick > NO_CURSOR_TIMEOUT_MS;
    }

    /// <summary>
    ///     Consumer poll while armaments are off: queue the re-arm once the cast is over, and let
    ///     go of the hold once the re-arm has run.
    /// </summary>
    private void ReEquipWhenCastIsOver()
    {
        if (_rearmItems.Count > 0)
        {
            if (Volatile.Read(ref _rearmsLeft[0]) <= 0)
                ReleaseHold();
            return;
        }

        bool? over = MainThreadQueue.BubblingInvokeOnMainThread(IsHeldCastOver, _cTokenSource.Token);
        if (over == null)
            ReleaseHold();
        else if (over.Value)
            EnqueueReEquip(_held);
    }

    /// <summary>
    ///     Forget the held armaments. They are back in hand, or there is no player to hand them to.
    /// </summary>
    private void ReleaseHold()
    {
        _held.Clear();
        _rearmItems.Clear();
        _rearmsLeft = null;
        _awaitingCast = false;
        _holdingArms = false;
    }

    /// <summary>
    ///     Cancel a re-arm that is queued but has not run, keeping the armaments held
    /// </summary>
    private void CancelQueuedReEquip()
    {
        foreach (ObjectActionQueueItem item in _rearmItems)
            item.SetCanceled();

        _rearmItems.Clear();
        _rearmsLeft = null;
    }

    /// <summary>
    ///     Enqueues un-equip actions, if the player is currently armed
    /// </summary>
    /// <returns></returns>
    private void EnqueueUnequip(IList<Armament> arms)
    {
        // Issue an unequip for each equipped armament.
        // Future compatability with Cephalopod based players.
        foreach (Armament arm in arms ?? [])
            ObjectActionQueue.Instance.Enqueue(
                CreateUnequipQueueItem(arm.Serial, arm.Layer),
                ActionPriority.EquipItem
            );
    }

    /// <summary>
    ///     Create an un-equip queue item, to be used with the object queue
    /// </summary>
    /// <param name="serial">The item to un-equip</param>
    /// <param name="layer">The item's layer, used for validation</param>
    /// <returns></returns>
    private ObjectActionQueueItem CreateUnequipQueueItem(uint serial, Layer layer) =>
        new(() =>
        {
            if (!IsPlayerBackpackAvailable)
                return;

            Item item = _world.Items?.Get(serial);
            if (item == null || item.Container != _world.Player.Serial || item.Layer != layer)
                return;

            new MoveRequest(serial, _world.Player.Backpack.Serial, item.Amount).Execute();
        });

    /// <summary>
    ///     Enqueues the given armaments for re-equipment
    /// </summary>
    /// <param name="arms">The armaments to re-equip</param>
    private void EnqueueReEquip(IList<Armament> arms)
    {
        int[] left = [arms.Count];
        _rearmsLeft = left;
        _awaitingCast = false;

        foreach (Armament arm in arms)
        {
            var queued = new ObjectActionQueueItem(() =>
            {
                Item item = _world?.Items?.Get(arm.Serial);
                Item backpackItem = _world?.Player?.Backpack;

                if (item != null && backpackItem != null && item.Container == backpackItem.Serial)
                    MoveRequest.EquipItem(arm.Serial, arm.Layer)?.Execute();
            }, _ => Interlocked.Decrement(ref left[0]));

            _rearmItems.Add(queued);
            ObjectActionQueue.Instance.Enqueue(queued, ActionPriority.EquipItem);
        }
    }

    #endregion

    #region Regular Expressions

    /// <summary>
    ///     A regex used to match an item's `Spell Channeling` property
    /// </summary>
    /// <returns></returns>
    [GeneratedRegex(@"^\s*Spell Channeling\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex IsSpellChannelling();

    /// <summary>
    ///     A regex used to match standard 'drinkable' potion names
    /// </summary>
    /// <returns></returns>
    [GeneratedRegex(@"(Strength|Agility|Heal|Cure|Nightsight|Refresh(ment)?)\s+Potion", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IsPotionRegex();

    #endregion
}
