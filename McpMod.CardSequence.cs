using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public static partial class McpMod
{
    private const int MaxSequencePlays = 6;
    private sealed record PlannedPlay(int? CardIndex, string? CardId, string? Target);
    private sealed record SequencePlay(CardModel Card, string CardId, string? Target);
    private sealed record SequencePlan(Player Player, ICombatState Combat, int Turn, HashSet<Creature> Enemies,
        List<SequencePlay> Plays, MultiplayerSequence? Multiplayer);

    private static async Task<Dictionary<string, object?>> ExecuteCardSequence(Dictionary<string, JsonElement> data,
        bool multiplayer = false)
    {
        if (!data.TryGetValue("plays", out var entries) || entries.ValueKind != JsonValueKind.Array ||
            entries.GetArrayLength() is < 1 or > MaxSequencePlays)
            return Error($"Provide between one and {MaxSequencePlays} card plays.");

        PlannedPlay?[]? plays;
        try { plays = entries.Deserialize<PlannedPlay?[]>(_jsonOptions); }
        catch (JsonException) { return Error("Each play must include card_index, card_id, and a target when required."); }
        if (plays == null || plays.Any(play => play?.CardIndex is not >= 0 || string.IsNullOrWhiteSpace(play.CardId)))
            return Error("Each play must include a non-negative card_index and a card_id from the starting hand.");

        if (data.TryGetValue("end_turn", out var endTurnValue) &&
            endTurnValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return Error("end_turn must be a boolean.");
        bool endTurn = endTurnValue.ValueKind == JsonValueKind.True;
        int? coordinationAfterId = null;
        string? turnId = null;
        TargetHpRange? hpRange = null;
        if (data.TryGetValue("target_hp_range", out var range))
        {
            try { hpRange = range.Deserialize<TargetHpRange>(_jsonOptions); }
            catch (JsonException) { return Error("Invalid target_hp_range."); }
            if (!multiplayer || hpRange == null || string.IsNullOrEmpty(hpRange.Target) ||
                hpRange.Min < 1 || hpRange.Max < hpRange.Min)
                return Error("Provide a planned target and a positive, inclusive HP range.");
        }
        if (multiplayer)
        {
            if (!data.TryGetValue("turn_id", out var turn) || turn.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(turnId = turn.GetString()))
                return Error("Provide turn_id from battle.sequence_turn_id in fresh multiplayer state.");
            if (data.TryGetValue("coordination_after_id", out var cursor))
            {
                if (cursor.ValueKind != JsonValueKind.Number || !cursor.TryGetInt32(out int parsed) || parsed < 0)
                    return Error("coordination_after_id must be a non-negative integer.");
                coordinationAfterId = parsed;
            }
            if (data.ContainsKey("coordination_prompt_id") || entries.EnumerateArray().Any(entry =>
                    entry.TryGetProperty("coordination_prompt_id", out _) || entry.TryGetProperty("target_player_slot", out _)))
                return Error("Use individual card plays for player targets or coordination-bound actions.");
        }

        bool decision = multiplayer && data.TryGetValue("view", out var view) &&
            view.ValueKind == JsonValueKind.String && view.GetString() == "decision";
        var submitted = new List<string>(plays.Length);
        var clock = Stopwatch.StartNew();
        ActionBlock? stop;
        MultiplayerSequence? mp = null;
        try
        {
            var bound = await RunOnMainThread(() => BindSequence(plays, turnId, coordinationAfterId, hpRange)).ConfigureAwait(false);
            var plan = bound.Plan;
            mp = plan?.Multiplayer;
            stop = bound.Block;
            if (plan != null)
            {
                foreach (var play in plan.Plays)
                {
                    if (clock.Elapsed >= TimeSpan.FromSeconds(7))
                    {
                        stop = new("state_wait_timed_out", "The card sequence reached its time limit. Refresh game state.", false);
                        break;
                    }

                    var wait = new CombatStateWait();
                    stop = await RunOnMainThread(() => SubmitSequenceCard(plan, play, wait)).ConfigureAwait(false);
                    if (stop != null)
                        break;

                    submitted.Add(play.CardId);
                    stop = await WaitForSequenceCard(plan, wait, clock).ConfigureAwait(false);
                    if (stop != null)
                        break;
                }

                if (stop == null && endTurn)
                {
                    var wait = new CombatStateWait();
                    stop = await RunOnMainThread(() => GetSequenceBlock(plan.Player, plan, multiplayer) ??
                        GetSequenceRejection(multiplayer ? ExecuteMultiplayerEndTurn(plan.Player, data) :
                            ExecuteEndTurn(plan.Player, wait), "end_turn_rejected")).ConfigureAwait(false);
                    if (stop == null)
                        return await BuildSequenceResult(submitted, true, null, multiplayer ? null : wait,
                            multiplayer, coordinationAfterId, decision).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] Card sequence interrupted: {ex}");
            stop = new("sequence_interrupted", "The next result is uncertain. Refresh state before continuing.", false);
        }
        finally
        {
            // Dispose only removes a managed event handler, even if the game stops pumping frames.
            mp?.Dispose();
        }

        return await BuildSequenceResult(submitted, false, stop, multiplayer: multiplayer,
            coordinationAfterId: coordinationAfterId, decision: decision).ConfigureAwait(false);
    }

    private static (SequencePlan? Plan, ActionBlock? Block) BindSequence(PlannedPlay?[] plays,
        string? turnId, int? coordinationAfterId, TargetHpRange? hpRange)
    {
        bool multiplayer = turnId != null;
        var run = RunManager.Instance.DebugOnlyGetState();
        var player = run == null ? null : LocalContext.GetMe(run);
        if (GetSequenceBlock(player, multiplayer: multiplayer) is { } block)
            return (null, block);
        if (player?.PlayerCombatState is not { } playerState || player.Creature.CombatState is not { } combat)
            return (null, new("not_in_combat", "No active player combat state.", false));
        if (turnId != null && CheckSequenceObservation(turnId, combat, player, hpRange) is { } stale)
            return (null, stale);

        if (hpRange != null && (ResolveTarget(combat, hpRange.Target) is not { } target ||
            target.CurrentHp < hpRange.Min || target.CurrentHp > hpRange.Max))
            return (null, new("target_hp_out_of_range", "The planned target is outside the accepted HP range.", false));
        var hand = playerState.Hand.Cards;
        var bound = new List<SequencePlay>(plays.Length);
        var usedIndices = new HashSet<int>();
        foreach (var requested in plays)
        {
            var play = requested!;
            int index = play.CardIndex!.Value;
            if (index >= hand.Count || !string.Equals(hand[index].Id.Entry, play.CardId, StringComparison.Ordinal) ||
                !usedIndices.Add(index))
                return (null, new("card_mismatch", "Each play must match a distinct card in the starting hand.", false));

            var card = hand[index];
            if (multiplayer && card.TargetType is TargetType.AnyAlly or TargetType.AnyPlayer)
                return (null, new("player_target_required", "Play cards that target a player individually.", false));
            if (card.TargetType == TargetType.AnyEnemy &&
                (play.Target == null || ResolveTarget(combat, play.Target) == null))
                return (null, new("target_mismatch", "A planned target is missing or is no longer alive.", false));
            bound.Add(new SequencePlay(card, card.Id.Entry, play.Target));
        }

        if (hpRange != null && !bound.Any(play => play.Card.TargetType == TargetType.AnyEnemy && play.Target == hpRange.Target))
            return (null, new("target_mismatch", "The HP range must name an enemy targeted by a planned card.", false));

        MultiplayerSequence? mp = null;
        if (multiplayer)
        {
            var chat = ReadCoordinationDelta(coordinationAfterId ?? int.MaxValue);
            int? latest = GetCoordinationCursor(chat);
            if (HasOpenCoordination(chat) ||
                coordinationAfterId is int cursor && latest is int current && current != cursor)
                return (null, CoordinationChanged);
            mp = new MultiplayerSequence(player, combat, hand, latest);
        }
        return (new(player, combat, playerState.TurnNumber,
            new HashSet<Creature>(GetLivingEnemies(combat).Select(entry => entry.Creature), ReferenceEqualityComparer.Instance),
            bound, mp), null);
    }

    private static ActionBlock? GetSequenceBlock(Player? player, SequencePlan? plan = null, bool multiplayer = false)
    {
        if (IsMultiplayerRun() != multiplayer)
            return new("game_mode_changed", "The expected run mode is no longer active.", false);
        if (IsMapScreenOpenOrVisible() || NOverlayStack.Instance?.Peek() != null || GetOpenModalNode() != null ||
            Engine.GetMainLoop() is SceneTree tree && IsAnyFtueVisible(tree.Root))
            return new("screen_changed", "A different screen is active.", false);
        if (GetCombatActionBlock(player) is { } block)
            return block;
        if (plan != null && (!ReferenceEquals(player?.Creature.CombatState, plan.Combat) ||
                            player?.PlayerCombatState?.TurnNumber != plan.Turn))
            return new("turn_changed", "The original combat turn ended.", false);
        if (HasNewInterruptingCombatWarning())
            return new("combat_warning", "A new danger warning needs attention.", false);
        if (plan != null && !plan.Enemies.SetEquals(GetLivingEnemies(plan.Combat).Select(entry => entry.Creature)))
            return new("enemies_changed", "The enemies changed. Check the new state before continuing.", false);
        if (plan?.Multiplayer is { } mp)
            return mp.Check(plan);
        return null;
    }

    private static ActionBlock? SubmitSequenceCard(SequencePlan plan, SequencePlay play, CombatStateWait wait)
    {
        if (GetSequenceBlock(plan.Player, plan, plan.Multiplayer != null) is { } block)
            return block;

        var hand = plan.Player.PlayerCombatState!.Hand.Cards;
        for (int i = 0; i < hand.Count; i++)
        {
            if (ReferenceEquals(hand[i], play.Card) && hand[i].Id.Entry == play.CardId)
                return GetSequenceRejection(ExecutePlayableCard(plan.Player, i, play.Target, wait,
                    onSubmitting: plan.Multiplayer == null ? null : plan.Multiplayer.Expect), "card_rejected");
        }

        return new("card_missing", "The planned card is no longer in the hand.", false);
    }

    private static ActionBlock? GetSequenceRejection(Dictionary<string, object?> result, string fallback) =>
        result.GetValueOrDefault("status") is "ok" ? null : new ActionBlock(
            result.GetValueOrDefault("reason") as string ?? fallback,
            result.GetValueOrDefault("error") as string ?? "The game rejected the action.", false);

    private static async Task<ActionBlock?> WaitForSequenceCard(SequencePlan plan, CombatStateWait wait, Stopwatch clock)
    {
        while (true)
        {
            var (pending, stop) = await RunOnMainThread(() =>
            {
                if (plan.Multiplayer is { } mp)
                    return mp.CheckSettlement(plan);
                if (IsWaitingForPlayableState(wait))
                    return (true, (ActionBlock?)null);
                if (wait.CardCompletion is { IsFaulted: true } or { IsCanceled: true })
                    return (false, new ActionBlock("card_interrupted", "The game interrupted the submitted card.", false));
                return (false, GetSequenceBlock(plan.Player, plan));
            }).ConfigureAwait(false);
            if (!pending)
                return stop;
            if (clock.Elapsed >= TimeSpan.FromSeconds(7))
                return new("state_wait_timed_out", "A submitted card may still be resolving. Refresh game state.", false);
            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<string, object?>> BuildSequenceResult(List<string> submitted, bool endTurn,
        ActionBlock? stop, CombatStateWait? wait = null, bool multiplayer = false, int? coordinationAfterId = null, bool decision = false)
    {
        var result = new Dictionary<string, object?>
        {
            ["status"] = stop == null ? "ok" : "stopped",
            ["submitted"] = submitted,
            ["end_turn_submitted"] = endTurn,
            ["stop_reason"] = stop?.Reason,
            ["message"] = stop?.Message,
            ["submitted_count"] = submitted.Count,
        };
        if (stop?.Changes != null) result["changes"] = stop.Changes;
        try
        {
            if (multiplayer)
            {
                var snapshot = await RunOnMainThread(() => BuildMultiplayerGameState(
                    GetPartyNameLanguage() ?? _configuredNameLanguage, compact: true, coordinationAfterId, decision || stop != null))
                    .ConfigureAwait(false);
                result["state"] = snapshot;
                if (stop != null)
                    AddRecovery(result, snapshot);
            }
            else if (wait == null)
            {
                result["state"] = await RunOnMainThread(() =>
                    BuildGameState(compact: true, nameLanguage: _configuredNameLanguage)).ConfigureAwait(false);
            }
            else
            {
                var ready = await ReadReadyState(compact: true, action: wait, nameLanguage: _configuredNameLanguage)
                    .ConfigureAwait(false);
                result["state"] = ready.State;
                if (ready.TimedOut)
                    result["state_wait_timed_out"] = true;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] Sequence state failed: {ex}");
            result["state_error"] = "Refresh game state before continuing.";
        }
        return result;
    }
}
