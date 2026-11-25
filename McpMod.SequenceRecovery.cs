using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace STS2_MCP;

public static partial class McpMod
{
    private sealed record StateChange(string EntityId, string Field, JsonNode? Before, JsonNode? After);
    private sealed record TargetHpRange(string Target, int Min, int Max)
    {
        public bool Accepts(StateChange change) =>
            change.Field == "hp" && change.EntityId == Target &&
            change.Before is JsonValue before && before.TryGetValue<int>(out var oldHp) && oldHp > 0 &&
            change.After is JsonValue after && after.TryGetValue<int>(out var hp) && hp >= Min && hp <= Max;
    }

    private static List<StateChange> ChangedEnemies(string previous, string current)
    {
        static Dictionary<string, JsonObject> Index(JsonArray list) => list.OfType<JsonObject>()
            .ToDictionary(enemy => (string)enemy["entity_id"]!);
        var previousList = JsonNode.Parse(previous)!.AsArray();
        var currentList = JsonNode.Parse(current)!.AsArray();
        var before = Index(previousList);
        var after = Index(currentList);
        var changes = new List<StateChange>();
        foreach (var id in before.Keys.Union(after.Keys))
        {
            if (!before.TryGetValue(id, out var oldEnemy) || !after.ContainsKey(id))
            {
                changes.Add(new(id, "presence", before.ContainsKey(id), after.ContainsKey(id)));
                continue;
            }
            var enemy = after[id];
            foreach (var key in oldEnemy.Select(p => p.Key).Union(enemy.Select(p => p.Key)))
                if (oldEnemy.ContainsKey(key) != enemy.ContainsKey(key) || !JsonNode.DeepEquals(oldEnemy[key], enemy[key]))
                    changes.Add(new(id, key, oldEnemy[key], enemy[key]));
        }
        if (!before.Keys.SequenceEqual(after.Keys))
            changes.Add(new("enemies", "order", null, null));
        return changes;
    }

    private static List<StateChange> ChangedFields(string entity, string previous, string current)
    {
        var before = JsonNode.Parse(previous)!.AsObject();
        var after = JsonNode.Parse(current)!.AsObject();
        return before.Select(p => p.Key).Union(after.Select(p => p.Key))
            .Where(k => before.ContainsKey(k) != after.ContainsKey(k) || !JsonNode.DeepEquals(before[k], after[k]))
            .Select(k => new StateChange(entity, k, before[k], after[k])).ToList();
    }

    private static void AddRecovery(Dictionary<string, object?> receipt, Dictionary<string, object?> state, string? action = null)
    {
        if (receipt.GetValueOrDefault("stop_reason") is "coordination_changed")
        {
            receipt["next_operation"] = "review_returned_coordination";
            return;
        }
        if (receipt.GetValueOrDefault("stop_reason") is "sequence_in_progress")
        {
            receipt["next_operation"] = "await_active_sequence_receipt";
            return;
        }
        if (state.GetValueOrDefault("hand_select") is Dictionary<string, object?> hand)
            receipt["next_tool"] = hand.GetValueOrDefault("can_confirm") is true
                ? hand.GetValueOrDefault("confirm_tool") : hand.GetValueOrDefault("select_tool");
        else if (state.GetValueOrDefault("card_select") is Dictionary<string, object?> choice)
            receipt["next_tool"] = choice.GetValueOrDefault("can_select") is false && choice.GetValueOrDefault("can_confirm") is true
                ? choice.GetValueOrDefault("confirm_tool") : choice.GetValueOrDefault("select_tool");
        else if (state.GetValueOrDefault("battle") is Dictionary<string, object?> battle)
            receipt["next_tool"] = battle.GetValueOrDefault("can_act") is true
                ? action switch { "end_turn" => "mp_combat_end_turn", "use_potion" => "mp_use_potion", _ => "mp_combat_play_card" }
                : "mp_combat_decision";
        else
            receipt["next_tool"] = "mp_get_game_state";
    }
}
