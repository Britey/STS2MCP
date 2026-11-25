using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public static partial class McpMod
{
    private sealed class CombatStateWait
    {
        internal Task? CardCompletion { get; set; }
        internal Func<bool>? MultiplayerPending { get; set; }
        internal int? EndedTurnNumber { get; set; }
        internal MapTravelWait? MapTravel { get; set; }
        internal HandConfirmationWait? HandConfirmation { get; set; }
    }

    private sealed record MapTravelWait(RunState Run, AbstractRoom? Origin, int ActIndex, MapCoord Destination);
    private sealed record HandConfirmationWait(NPlayerHand Hand, object? Prompt);

    private static object? GetHandSelectionPrompt(NPlayerHand hand) =>
        GetInstanceFieldValue(hand, "_selectionCompletionSource");

    private static async Task<(Dictionary<string, object?> State, bool TimedOut)> ReadReadyState(
        bool compact, CombatStateWait? action = null, string? nameLanguage = null,
        bool advanceForcedMap = false, bool waitForReady = true)
    {
        var started = Stopwatch.GetTimestamp();
        var deadline = TimeSpan.FromSeconds(7);
        Dictionary<string, object?>? advance = null;
        while (true)
        {
            var result = await RunOnMainThread(() =>
            {
                if (advanceForcedMap)
                {
                    advanceForcedMap = false;
                    advance = TryAdvanceForcedMap(multiplayer: false, action ??= new());
                }
                bool waiting = (waitForReady || advance != null) && IsWaitingForPlayableState(action);
                bool timedOut = waiting && Stopwatch.GetElapsedTime(started) >= deadline;
                var state = !waiting || timedOut ? BuildGameState(compact, nameLanguage) : null;
                if (state != null && advance != null)
                {
                    state["auto_map"] = advance;
                    if (timedOut)
                        state["state_wait_timed_out"] = true;
                }
                return (State: state, TimedOut: timedOut);
            }).ConfigureAwait(false);
            if (result.State != null)
                return (result.State, result.TimedOut);

            // Probe native controls without rebuilding the hand, piles, and scene payload each time.
            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<string, object?>> ReadMultiplayerState(bool compact,
        string? nameLanguage, bool usePartyLanguage, int? coordinationAfterId, bool advanceForcedMap, bool waitForReady,
        bool decision = false, int decisionWaitSeconds = 0)
    {
        var started = Stopwatch.GetTimestamp();
        Dictionary<string, object?>? advance = null;
        while (true)
        {
            var state = await RunOnMainThread(() =>
            {
                if (advanceForcedMap)
                {
                    advanceForcedMap = false;
                    advance = TryAdvanceForcedMap(multiplayer: true);
                }
                bool timedOut = false;
                if (decisionWaitSeconds > 0)
                {
                    if (ShouldWaitForCombatDecision(coordinationAfterId ?? 0))
                    {
                        if (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(decisionWaitSeconds))
                            return null;
                        timedOut = true;
                    }
                }
                else if (Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(1500))
                {
                    if ((waitForReady || advance != null) &&
                        ShouldWaitForMultiplayerState(waitForReady, advance != null))
                        return null;
                    if (decision && OpeningHandIsBeingDealt() &&
                        !HasDecisionCoordination(coordinationAfterId ?? 0))
                        return null;
                }

                var snapshot = BuildMultiplayerGameState(usePartyLanguage ? GetPartyNameLanguage() ?? nameLanguage :
                    nameLanguage, compact, coordinationAfterId, decision);
                if (advance != null)
                    snapshot["auto_map"] = advance;
                if (timedOut)
                    snapshot["state_wait_timed_out"] = true;
                return snapshot;
            }).ConfigureAwait(false);
            if (state != null)
                return state;
            await Task.Delay(decisionWaitSeconds > 0 ? 100 : 25).ConfigureAwait(false);
        }
    }

    private static bool ShouldWaitForCombatDecision(int cursor)
    {
        if (NOverlayStack.Instance?.Peek() is MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NChooseACardSelectionScreen choose)
            return !ChooseCardAcceptsInput(choose) && !HasDecisionCoordination(cursor);
        if (!IsMultiplayerRun() || !CombatManager.Instance.IsInProgress ||
            RunManager.Instance.DebugOnlyGetState() is not { } run ||
            IsMapScreenOpenOrVisible() || NOverlayStack.Instance?.Peek() != null || GetOpenModalNode() != null)
            return false;

        var block = GetCombatActionBlock(LocalContext.GetMe(run));
        if (block == null || (!block.Retryable && block.Reason is not ("turn_already_ended" or "end_turn_pending")))
            return false;
        var chat = ReadCoordinationDelta(cursor);
        if ((GetCoordinationCursor(chat) is int latest && latest != cursor) || HasIncomingCoordinationPrompt(chat))
            return false;
        return Godot.Engine.GetMainLoop() is Godot.SceneTree tree && !IsAnyFtueVisible(tree.Root);
    }

    private static bool ShouldWaitForMultiplayerState(bool waitForCombat, bool waitForMapTravel)
    {
        if (!IsMultiplayerRun())
            return false;
        if (waitForMapTravel && IsMultiplayerMapTraveling())
            return true;
        if (!waitForCombat || !CombatManager.Instance.IsInProgress ||
            IsMapScreenOpenOrVisible() || NOverlayStack.Instance?.Peek() != null || GetOpenModalNode() != null ||
            RunManager.Instance.DebugOnlyGetState() is not { } run)
            return false;
        return GetCombatActionBlock(LocalContext.GetMe(run)) is { Retryable: true };
    }

    private static bool IsWaitingForPlayableState(CombatStateWait? action)
    {
        var run = RunManager.Instance;
        if (!run.IsInProgress || IsMultiplayerRun())
            return false;

        var state = run.DebugOnlyGetState();
        bool mapIsOpen = IsMapScreenOpenOrVisible();
        bool overlayIsOpen = NOverlayStack.Instance?.Peek() != null;
        bool modalIsOpen = GetOpenModalNode() != null;
        if (action?.MapTravel is { } travel)
        {
            if (state == null || !ReferenceEquals(state, travel.Run) || state.CurrentActIndex != travel.ActIndex)
                return false; // The original run or act is gone.

            var room = state.CurrentRoom;
            bool reached = state.CurrentMapCoord == travel.Destination &&
                room != null && !ReferenceEquals(room, travel.Origin);
            if (modalIsOpen || (reached && !mapIsOpen &&
                (overlayIsOpen || NPlayerHand.Instance?.IsInCardSelection == true)))
                return false; // Return an input screen even if room entry is waiting on it.

            if (!reached || run.IsChangingRooms || NMapScreen.Instance?.IsTraveling == true ||
                (mapIsOpen && room is not MapRoom))
                return true;
        }

        if (mapIsOpen || overlayIsOpen || modalIsOpen)
            return false;

        if (state?.CurrentRoom is TreasureRoom)
        {
            var treasure = GetVisibleTreasureRoom();
            return treasure == null || TryOpenTreasureChest(treasure) ||
                (GetAvailableTreasureProceedButton(treasure) == null && GetAvailableTreasureRelicHolders(treasure).Count == 0);
        }

        if (state?.CurrentRoom is not CombatRoom)
            return false;
        if (!CombatManager.Instance.IsInProgress)
            return true; // The map, rewards, or game-over screen has yet to appear.

        var player = LocalContext.GetMe(state);
        var block = GetCombatActionBlock(player);
        if (block is { Reason: "hand_selection" } && action?.HandConfirmation is { } confirmation)
        {
            // Confirm can complete the prompt's source before the hand has left selection mode.
            // A different source represents a new prompt, even on the same hand.
            return ReferenceEquals(NPlayerHand.Instance, confirmation.Hand) &&
                ReferenceEquals(GetHandSelectionPrompt(confirmation.Hand), confirmation.Prompt);
        }
        if (block is { Retryable: false } && block.Reason != "turn_already_ended")
            return false; // A hand selection or other input can interrupt the pending action.

        return block != null || action?.CardCompletion is { IsCompleted: false } ||
            (action?.EndedTurnNumber is int turn && player?.PlayerCombatState?.TurnNumber == turn) ||
            (action?.HandConfirmation != null && !run.ActionQueueSet.IsEmpty);
    }
}
