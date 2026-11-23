using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public static partial class McpMod
{
    private static int _multiplayerSequenceActive;
    private static bool MultiplayerSequenceActive => Volatile.Read(ref _multiplayerSequenceActive) != 0;
    private static readonly ActionBlock MultiplayerSequenceInProgress = new("sequence_in_progress",
        "Wait for the other local multiplayer sequence to finish, then check its receipt or refresh state.", true);
    private static readonly ActionBlock CoordinationChanged = new("coordination_changed",
        "Read the coordination update and handle any open prompt before planning more actions.", false);
    private static readonly ConditionalWeakTable<ICombatState, string> SequenceCombatIds = new();

    private static string GetSequenceTurnId(ICombatState combat, Player player) =>
        $"{SequenceCombatIds.GetValue(combat, _ => Guid.NewGuid().ToString("N"))}:" +
        $"{player.PlayerCombatState!.TurnNumber}:{RunManager.Instance.ActionQueueSet.NextActionId}";

    // Preserve the evidence the caller actually saw, including modded card text, costs,
    // legal targets and statuses. Ignore end-turn votes when comparing the party.
    private sealed record SequenceObservation(string Revision, string? Language, CardModel[] Hand,
        int LocalPlays, string LocalState, string Enemies, string Party, HashSet<CombatWarning> Warnings)
    {
        internal ActionBlock? Compare(SequenceObservation current, TargetHpRange? hpRange = null)
        {
            if (LocalPlays != current.LocalPlays)
                return new("local_play_changed", "Another local card play occurred since this snapshot.", false,
                    [new("local_player", "plays", LocalPlays, current.LocalPlays)]);
            if (!Hand.SequenceEqual(current.Hand, ReferenceEqualityComparer.Instance))
                return new("hand_changed", "The local hand changed since this snapshot.", false,
                    [new("local_player", "hand", JsonSerializer.SerializeToNode(Hand.Select(c => c.Id.Entry)),
                        JsonSerializer.SerializeToNode(current.Hand.Select(c => c.Id.Entry)))]);
            if (Enemies != current.Enemies)
            {
                var changes = ChangedEnemies(Enemies, current.Enemies);
                if (hpRange == null || changes.Count != 1 || !hpRange.Accepts(changes[0]))
                    return new("enemies_changed", "Enemy state changed since the planning snapshot.", false, changes);
            }
            if (!Warnings.SetEquals(current.Warnings))
                return new("combat_warning", "Combat warnings changed since this snapshot.", false,
                    [new("battle", "warnings", JsonSerializer.SerializeToNode(Warnings), JsonSerializer.SerializeToNode(current.Warnings))]);
            if (LocalState != current.LocalState || Party != current.Party)
            {
                var changes = ChangedFields("local_player", LocalState, current.LocalState);
                if (Party != current.Party)
                {
                    var before = JsonSerializer.Deserialize<JsonElement[]>(Party)!;
                    var after = JsonSerializer.Deserialize<JsonElement[]>(current.Party)!;
                    if (before.Length == after.Length)
                        for (int i = 0; i < before.Length; i++)
                            changes.AddRange(ChangedFields($"player_slot_{i}", before[i].GetRawText(), after[i].GetRawText()));
                    else changes.Add(new("party", "player_count", before.Length, after.Length));
                }
                return new("player_state_changed", "Player resources, card effects or party state changed.", false, changes);
            }
            return null;
        }
    }

    private static readonly ConditionalWeakTable<ICombatState, List<(string Token, SequenceObservation Snapshot)>> SequenceObservations = new();

    private static SequenceObservation CaptureSequenceObservation(Player player, ICombatState combat, string revision,
        string? language, object? localState, object? enemies, List<Dictionary<string, object?>> players)
    {
        var party = players.Select(member =>
            member.Where(field => field.Key != "is_ready_to_end_turn").ToDictionary());
        return new(revision, language, player.PlayerCombatState!.Hand.Cards.ToArray(),
            CombatManager.Instance.History.CardPlaysStarted.Count(play => play.CardPlay.Player == player),
            JsonSerializer.Serialize(localState), JsonSerializer.Serialize(enemies), JsonSerializer.Serialize(party),
            GetCurrentCombatWarnings(combat, player.Creature).Where(w => w.InterruptSequence).ToHashSet());
    }

    private static void RememberSequenceObservation(Player player, string? language, Dictionary<string, object?> state)
    {
        if (player.Creature.CombatState is not { } combat ||
            state.GetValueOrDefault("battle") is not Dictionary<string, object?> battle ||
            battle.GetValueOrDefault("can_act") is not true || battle.GetValueOrDefault("sequence_turn_id") is not string revision ||
            state.GetValueOrDefault("players") is not List<Dictionary<string, object?>> players)
            return;
        var cache = SequenceObservations.GetOrCreateValue(combat);
        var current = CaptureSequenceObservation(player, combat, revision, language, state["player"], battle["enemies"], players);
        var last = cache.LastOrDefault();
        if (last.Snapshot != null && last.Snapshot.Revision == revision && last.Snapshot.Language == language &&
            last.Snapshot.Compare(current) == null)
        {
            battle["sequence_turn_id"] = last.Token;
            return;
        }
        if (cache.Count >= 32)
            cache.RemoveAt(0);
        var token = $"{revision}:{Guid.NewGuid():N}";
        cache.Add((token, current));
        battle["sequence_turn_id"] = token;
    }

    private static ActionBlock? CheckSequenceObservation(string token, ICombatState combat, Player player, TargetHpRange? hpRange)
    {
        string revision = GetSequenceTurnId(combat, player);
        // General state and older clients can supply the original exact-queue token.
        if (token == revision)
            return null;
        if (!token.StartsWith(revision[..(revision.LastIndexOf(':') + 1)], StringComparison.Ordinal))
            return new("turn_changed", "The planning snapshot belongs to a different combat or local turn.", false);
        if (!SequenceObservations.TryGetValue(combat, out var cache) || cache.Find(entry => entry.Token == token).Snapshot is not { } previous)
            return new("snapshot_stale", "The shared queue advanced or the snapshot expired. Use the returned decision state.", false);

        var run = RunManager.Instance.DebugOnlyGetState()!;
        var current = CaptureSequenceObservation(player, combat, revision, previous.Language,
            BuildPlayerState(player, compact: true, previous.Language, decision: true),
            BuildEnemiesState(combat, previous.Language), BuildAllPlayersState(run, previous.Language));
        return previous.Compare(current, hpRange);
    }

    private static int? GetCoordinationCursor(Dictionary<string, object?> chat) =>
        chat.GetValueOrDefault("latest_id") is JsonElement { ValueKind: JsonValueKind.Number } value ? value.GetInt32() : null;

    private static bool HasOpenCoordination(Dictionary<string, object?> chat) =>
        new[] { "incoming_prompt", "outgoing_prompt", "outgoing_poll" }.Any(key =>
            chat.GetValueOrDefault(key) is JsonElement { ValueKind: JsonValueKind.Object } prompt &&
            prompt.TryGetProperty("status", out var status) &&
            status.GetString() is "pending" or "waiting_for_teammate" or "waiting_for_host");

    // The guest forwards an action to the host and later receives the synchronized copy.
    // Observe the queue on the game thread and wait for that copy on either seat.
    private sealed class MultiplayerSequence : IDisposable
    {
        private readonly ActionQueueSet _queue = RunManager.Instance.ActionQueueSet;
        private readonly HashSet<CardModel> _startingHand;
        private readonly HashSet<CombatWarning> _startingWarnings;
        private readonly int? _coordinationCursor;
        private PlayCardAction? _expectedCard;
        private GameAction? _enqueuedCard;
        private bool _externalAction;

        internal MultiplayerSequence(Player player, ICombatState combat, IEnumerable<CardModel> hand, int? cursor)
        {
            _startingHand = new(hand, ReferenceEqualityComparer.Instance);
            _startingWarnings = GetCurrentCombatWarnings(combat, player.Creature).ToHashSet();
            _coordinationCursor = cursor;
            _queue.ActionEnqueued += OnEnqueued;
        }

        internal void Expect(PlayCardAction card)
        {
            _expectedCard = card;
            _enqueuedCard = null;
        }

        private void OnEnqueued(GameAction action)
        {
            if (_expectedCard is { } expected && _enqueuedCard == null && action is PlayCardAction play &&
                play.OwnerId == expected.OwnerId && play.NetCombatCard.Equals(expected.NetCombatCard) &&
                play.TargetId == expected.TargetId)
            {
                _enqueuedCard = action;
                return;
            }
            _externalAction = true;
        }

        internal ActionBlock? Check(SequencePlan plan)
        {
            if (_externalAction)
                return new("other_action", "Another action entered the shared combat queue. Refresh before continuing.", false);
            if (GetCurrentCombatWarnings(plan.Combat, plan.Player.Creature)
                .Any(warning => warning.InterruptSequence && !_startingWarnings.Contains(warning)))
                return new("combat_warning", "A new danger warning needs attention.", false);
            if (plan.Player.PlayerCombatState!.Hand.Cards.Any(card => !_startingHand.Contains(card)))
                return new("hand_changed", "A card was drawn or created. Check the new hand before continuing.", false);
            var chat = ReadCoordinationDelta(int.MaxValue);
            if (GetCoordinationCursor(chat) != _coordinationCursor || HasOpenCoordination(chat))
                return CoordinationChanged;
            return null;
        }

        internal (bool Pending, ActionBlock? Stop) CheckSettlement(SequencePlan plan)
        {
            var readiness = GetSequenceBlock(plan.Player, plan, multiplayer: true);
            if (readiness is { Reason: not ("action_resolving" or "hand_not_ready") })
                return (false, readiness);
            if (_enqueuedCard is { } card &&
                (card.State == GameActionState.Canceled || card.CompletionTask.IsFaulted || card.CompletionTask.IsCanceled))
                return (false, new("card_interrupted", "The game interrupted the submitted card.", false));
            if (readiness != null || _enqueuedCard is not { State: GameActionState.Finished })
                return (true, null);
            _expectedCard = null;
            return (false, null);
        }

        public void Dispose() => _queue.ActionEnqueued -= OnEnqueued;
    }
}
