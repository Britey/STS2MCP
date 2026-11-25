using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

namespace STS2_MCP;

public static partial class McpMod
{
    private static bool OpeningHandIsBeingDealt()
    {
        if (!CombatManager.Instance.IsInProgress || !IsMultiplayerRun() ||
            CombatManager.Instance.DebugOnlyGetState()?.RoundNumber > 1 ||
            NOverlayStack.Instance?.Peek() != null || NPlayerHand.Instance?.IsInCardSelection == true ||
            GetOpenModalNode() != null || IsMapScreenOpenOrVisible() ||
            RunManager.Instance.DebugOnlyGetState() is not { } run)
            return false;
        var player = LocalContext.GetMe(run);
        return player?.PlayerCombatState?.Hand.Cards.Count == 0 &&
            GetCombatActionBlock(player)?.Reason is "turn_transition" or "hand_not_ready" or "player_not_ready";
    }

    private static bool HasDecisionCoordination(int cursor)
    {
        var chat = ReadCoordinationDelta(cursor);
        return GetCoordinationCursor(chat) > cursor || HasOpenCoordination(chat);
    }

    private static void AddDecisionTargets(Dictionary<string, object?> item, Player player,
        TargetType type, Func<Creature, bool>? isValid = null)
    {
        if (player.Creature.CombatState is not { } combat)
            return;
        try
        {
            if (type == TargetType.AnyEnemy)
                item["legal_enemy_ids"] = GetLivingEnemies(combat)
                    .Where(e => isValid?.Invoke(e.Creature) ?? true).Select(e => e.EntityId).ToArray();
            else if (type is TargetType.AnyAlly or TargetType.AnyPlayer)
                item["legal_player_slots"] = Enumerable.Range(0, player.RunState.Players.Count)
                    .Where(i => ResolvePlayerTarget(player, type, i) is { } creature &&
                        (isValid?.Invoke(creature) ?? true)).ToArray();
        }
        catch (Exception)
        {
            // A custom targeting rule can require UI context. Keep its description and target type.
            item["targeting_unavailable"] = true;
        }
    }

    private static void AttachDecisionWarningTargets(Dictionary<string, object?> state)
    {
        if (state.GetValueOrDefault("battle") is not Dictionary<string, object?> battle ||
            battle.GetValueOrDefault("warnings") is not List<Dictionary<string, object?>> warnings ||
            battle.GetValueOrDefault("enemies") is not List<Dictionary<string, object?>> enemies)
            return;
        foreach (var warning in warnings.Where(w => w.GetValueOrDefault("side") is "enemy"))
        {
            var ids = enemies.Where(enemy => warning.TryGetValue("entity_id", out var entity)
                    ? Equals(enemy["entity_id"], entity)
                    : enemy["status"] is List<Dictionary<string, object?>> effects &&
                        effects.Any(effect => Equals(effect.GetValueOrDefault("id"), warning["effect_id"])))
                .Select(enemy => enemy["entity_id"]).ToArray();
            if (ids.Length > 0)
                warning["entity_ids"] = ids;
        }
    }

    private static async Task AttachActionDecision(Dictionary<string, object?> receipt, CombatStateWait observation, int cursor, string action)
    {
        var started = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                var snapshot = await RunOnMainThread(() =>
                {
                    bool pending = receipt.GetValueOrDefault("status") is "ok" &&
                        IsMultiplayerRun() && observation.MultiplayerPending?.Invoke() == true;
                    bool choice = NPlayerHand.Instance?.IsInCardSelection == true ||
                        NOverlayStack.Instance?.Peek() != null || GetOpenModalNode() != null;
                    if (pending && !choice && started.ElapsedMilliseconds < 1500)
                        return null;
                    if (pending)
                        receipt["action_pending"] = true;
                    return BuildMultiplayerGameState(GetPartyNameLanguage() ?? _configuredNameLanguage,
                        compact: true, coordinationAfterId: cursor, decision: true);
                }).ConfigureAwait(false);
                if (snapshot != null)
                {
                    receipt["state"] = snapshot;
                    if (receipt.GetValueOrDefault("status") is "error") AddRecovery(receipt, snapshot, action);
                    return;
                }
                await Task.Delay(25).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] Action decision snapshot failed: {ex}");
            receipt["state_error"] = "Read fresh state before continuing. Keep the original action receipt.";
        }
    }
}
