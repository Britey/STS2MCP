using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace STS2_MCP;

public static partial class McpMod
{
    private sealed record Award(int? PlayerSlot, string Outcome, string? RelicId, string? RelicName);
    private static readonly ConditionalWeakTable<TreasureRoom, List<Award>> TreasureAwards = new();

    [HarmonyPatch(typeof(NTreasureRoomRelicCollection), "OnRelicsAwarded")]
    private static class RememberTreasureAwards
    {
        private static void Prefix(List<RelicPickingResult> __0)
        {
            var run = RunManager.Instance.DebugOnlyGetState();
            if (run?.CurrentRoom is not TreasureRoom room) return;
            TreasureAwards.AddOrUpdate(room, __0.Select(r => new Award(
                r.player == null ? null : run.Players.ToList().IndexOf(r.player), r.type.ToString(),
                r.relic?.Id.Entry, SafeGetText(() => r.relic?.Title))).ToList());
        }
    }

    // The loss shake also advances a rumble in a per-frame loop independently of its tween.
    [HarmonyPatch(typeof(NHandImage), "DoLoseShake")]
    private static class InstantTreasureLossShake
    {
        private static void Prefix(ref float duration)
        {
            if (SaveManager.Instance.PrefsSave.FastMode == FastModeType.Instant)
                duration = System.Math.Min(duration, 1f / 60f);
        }
    }

    // Scale only the treasure presentation. Godot still runs its callbacks,
    // finishes each tween, and resumes the native award and proceed code.
    [HarmonyPatch]
    private static class InstantTreasureTweens
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(NHandImage), "DoFightMove");
            yield return AccessTools.Method(typeof(NTreasureRoomRelicCollection), "AnimIn");
            yield return AccessTools.Method(typeof(NTreasureRoomRelicCollection), "AnimOut");
            yield return AccessTools.AsyncMoveNext(AccessTools.Method(typeof(NTreasureRoomRelicCollection), "AnimateRelicAwards"));
            foreach (var method in new[] { "GrabRelic", "DoLoseShake" })
                yield return AccessTools.AsyncMoveNext(AccessTools.Method(typeof(NHandImage), method));
        }

        private static Tween ApplySpeed(Tween tween) =>
            SaveManager.Instance.PrefsSave.FastMode == FastModeType.Instant ? tween.SetSpeedScale(100) : tween;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var create = AccessTools.Method(typeof(Node), nameof(Node.CreateTween));
            var speed = AccessTools.Method(typeof(InstantTreasureTweens), nameof(ApplySpeed));
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (instruction.Calls(create)) yield return new CodeInstruction(OpCodes.Call, speed);
            }
        }
    }
}
