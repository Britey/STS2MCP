using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using Sts2Localization;

namespace STS2_MCP;

public static partial class McpMod
{
    private static NFastModeTickbox? _instantModeTickbox;
    private static NFastModeTickbox? _originalFastModeTickbox;

    private static bool IsInInstantModeLine(Node node)
    {
        var current = node;
        while (current != null)
        {
            if (current.Name == "InstantMode") return true;
            current = current.GetParent();
        }
        return false;
    }

    private static T? FindChildOfType<T>(Node parent) where T : class
    {
        foreach (var child in parent.GetChildren())
        {
            if (child is T typed) return typed;
            if (child is Node childNode)
            {
                var found = FindChildOfType<T>(childNode);
                if (found != null) return found;
            }
        }
        return null;
    }

    private static void InjectInstantModeCheckbox(NSettingsScreen settingsScreen)
    {
        _instantModeTickbox = null;
        _originalFastModeTickbox = null;

        var content = settingsScreen.GetNode<NSettingsPanel>("%GeneralSettings").Content;
        var fastModeLine = content.GetNodeOrNull("FastMode");
        if (fastModeLine == null)
        {
            GD.PrintErr("[STS2 MCP] Could not find FastMode settings line");
            return;
        }

        _originalFastModeTickbox = FindChildOfType<NFastModeTickbox>(fastModeLine);

        // Duplicate the entire FastMode line and rename
        var instantLine = (Node)fastModeLine.Duplicate();
        instantLine.Name = "InstantMode";

        // Insert right after FastMode in the VBoxContainer
        int idx = fastModeLine.GetIndex();
        content.AddChild(instantLine);
        content.MoveChild(instantLine, idx + 1);

        // Update label text
        var label = instantLine.GetNodeOrNull<MegaRichTextLabel>("Label");
        if (label != null) label.Text = "Instant Mode";

        // Store reference and correct initial tick state
        // (NFastModeTickbox._Ready() already ran via AddChild, but used original SetFromSettings logic)
        _instantModeTickbox = FindChildOfType<NFastModeTickbox>(instantLine);
        if (_instantModeTickbox != null)
            _instantModeTickbox.IsTicked = SaveManager.Instance.PrefsSave.FastMode == FastModeType.Instant;

        GD.Print("[STS2 MCP] Instant Mode checkbox injected into settings");
    }

    private static void InjectNameLanguageSetting(NSettingsScreen settingsScreen)
    {
        var content = settingsScreen.GetNodeOrNull<NSettingsPanel>("%GeneralSettings")?.Content;
        if (content == null || content.HasNode("McpLanguage"))
            return;

        var row = new HBoxContainer { Name = "McpLanguage" };
        var label = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var picker = new OptionButton { Name = "LanguagePicker" };
        row.AddChild(label);
        row.AddChild(picker);
        content.AddChild(row);
        if (!picker.GetThemeFont("font").HasChar('简') &&
            ResourceLoader.Load<Font>("res://fonts/zhs/NotoSansMonoCJKsc-Regular.otf") is { } chineseFont)
        {
            var font = new FontVariation { BaseFont = picker.GetThemeFont("font"), Fallbacks = [chineseFont] };
            label.AddThemeFontOverride("font", font);
            picker.AddThemeFontOverride("font", font);
            picker.GetPopup().AddThemeFontOverride("font", font);
        }
        if (content.GetNodeOrNull("FastMode") is Control fastMode)
        {
            row.CustomMinimumSize = new Vector2(0, fastMode.Size.Y);
            content.MoveChild(row, fastMode.GetIndex() + 1);
        }

        string?[] languages = [null, "eng", GameNameLocalization.SimplifiedChinese];
        void RefreshLabels()
        {
            bool zh = LocManager.Instance.Language == GameNameLocalization.SimplifiedChinese;
            label.Text = zh ? "MCP 语言" : "MCP Language";
            picker.TooltipText = zh ? "设置 MCP 中的敌人和状态名称及 AI 沟通语言。房主选择英语或简体中文时，全队使用该语言。"
                : "Sets the language for MCP enemy and status names and AI coordination. In co-op, the host's English or Simplified Chinese choice applies to everyone.";
            picker.Clear();
            picker.AddItem(zh ? "跟随游戏" : "Follow Game");
            picker.AddItem("English");
            picker.AddItem("简体中文");
            picker.Select(Array.IndexOf(languages, _configuredNameLanguage));
        }

        RefreshLabels();
        settingsScreen.SettingsOpened += RefreshLabels;
        picker.ItemSelected += index =>
        {
            if (SaveNameLanguage(languages[index]))
                return;
            picker.Select(Array.IndexOf(languages, _configuredNameLanguage));
            picker.TooltipText = LocManager.Instance.Language == GameNameLocalization.SimplifiedChinese
                ? "无法将语言设置保存到 STS2_MCP.conf。"
                : "Could not save the language to STS2_MCP.conf.";
        };
    }

    // --- Harmony Patches ---

    [HarmonyPatch(typeof(NSettingsScreen), "_Ready")]
    static class SettingsScreenReadyPatch
    {
        static void Postfix(NSettingsScreen __instance)
        {
            try { InjectInstantModeCheckbox(__instance); }
            catch (Exception ex) { GD.PrintErr($"[STS2 MCP] Settings UI injection failed: {ex}"); }
            try { InjectNameLanguageSetting(__instance); }
            catch (Exception ex) { GD.PrintErr($"[STS2 MCP] Language settings UI injection failed: {ex}"); }
        }
    }

    [HarmonyPatch(typeof(NFastModeTickbox), nameof(NFastModeTickbox.SetFromSettings))]
    static class FastModeSetFromSettingsPatch
    {
        static bool Prefix(NFastModeTickbox __instance)
        {
            if (!IsInInstantModeLine(__instance)) return true;
            __instance.IsTicked = SaveManager.Instance.PrefsSave.FastMode == FastModeType.Instant;
            return false;
        }
    }

    [HarmonyPatch(typeof(NFastModeTickbox), "OnTick")]
    static class FastModeOnTickPatch
    {
        static bool Prefix(NFastModeTickbox __instance)
        {
            if (!IsInInstantModeLine(__instance)) return true;
            // Instant Mode turned on
            SaveManager.Instance.PrefsSave.FastMode = FastModeType.Instant;
            // Ensure the original Fast Mode checkbox also shows ticked
            if (_originalFastModeTickbox != null && !_originalFastModeTickbox.IsTicked)
                _originalFastModeTickbox.IsTicked = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(NFastModeTickbox), "OnUntick")]
    static class FastModeOnUntickPatch
    {
        static bool Prefix(NFastModeTickbox __instance)
        {
            if (!IsInInstantModeLine(__instance)) return true;
            // Instant Mode turned off - fall back to Fast
            SaveManager.Instance.PrefsSave.FastMode = FastModeType.Fast;
            return false;
        }

        static void Postfix(NFastModeTickbox __instance)
        {
            // Only for the ORIGINAL Fast Mode tickbox being unticked (→ Normal)
            if (IsInInstantModeLine(__instance)) return;
            if (_instantModeTickbox != null && _instantModeTickbox.IsTicked)
                _instantModeTickbox.IsTicked = false;
        }
    }

    [HarmonyPatch(typeof(NFastModeHoverTip), "_Ready")]
    static class InstantModeHoverTipPatch
    {
        static void Postfix(NFastModeHoverTip __instance)
        {
            if (!IsInInstantModeLine(__instance)) return;

            // Build a HoverTip with custom strings via reflection
            // (HoverTip is a record struct; Title/Description have private setters)
            object boxed = (object)default(HoverTip);
            var ht = typeof(HoverTip);
            ht.GetProperty("Id")!.SetValue(boxed, "sts2mcp_instant_mode");
            ht.GetProperty("Title")!.GetSetMethod(true)!
                .Invoke(boxed, ["Instant Mode"]);
            ht.GetProperty("Description")!.GetSetMethod(true)!
                .Invoke(boxed, [
                    "This option is added by the STS2MCP mod.\n" +
                    "When enabled, most in-game animations will become instant.\n" +
                    "Can reduce token usage as get-state calls will no longer receive an intermediate state."
                ]);

            // Replace the _hoverTip field set by the original _Ready
            typeof(NFastModeHoverTip)
                .GetField("_hoverTip", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(__instance, (IHoverTip)boxed);
        }
    }
}
