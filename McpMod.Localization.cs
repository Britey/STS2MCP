using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Combat;
using Sts2Localization;

namespace STS2_MCP;

public static partial class McpMod
{
    private static volatile string? _configuredNameLanguage;

    // The co-op companion reads the host's setting through this optional bridge.
    public static string? GetConfiguredNameLanguage() => _configuredNameLanguage;

    private static bool TryParseNameLanguage(string? value, out string? nameLanguage)
    {
        string? choice = value?.Trim().ToLowerInvariant();
        nameLanguage = choice switch
        {
            "en" or "eng" => "eng",
            "zh-cn" or GameNameLocalization.SimplifiedChinese => GameNameLocalization.SimplifiedChinese,
            _ => null
        };
        return nameLanguage != null || choice is null or "game";
    }

    private static string NameLanguageSetting(string? nameLanguage) => nameLanguage switch
    {
        "eng" => "en",
        GameNameLocalization.SimplifiedChinese => "zh-CN",
        _ => "game"
    };

    private static bool TryReadCombatNameLanguage(HttpListenerRequest request, HttpListenerResponse response,
        out string? nameLanguage)
    {
        if (request.QueryString["language"] is not { } language)
        {
            nameLanguage = _configuredNameLanguage;
            return true;
        }
        if (TryParseNameLanguage(language, out nameLanguage))
            return true;

        SendError(response, 400, "Choose game, en, or zh-CN for language.");
        return false;
    }

    private static bool SaveNameLanguage(string? nameLanguage)
    {
        string? temporaryPath = null;
        try
        {
            string path = GetConfigPath();
            var config = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject()
                : new JsonObject { ["port"] = DefaultPort, ["allow_profile_delete"] = false };
            config["language"] = NameLanguageSetting(nameLanguage);
            temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryPath, config.ToJsonString(_jsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
            _configuredNameLanguage = nameLanguage;
            return true;
        }
        catch (Exception ex)
        {
            Godot.GD.PrintErr($"[STS2 MCP] Failed to save language: {ex.Message}");
            return false;
        }
        finally
        {
            if (temporaryPath != null)
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }

    private static void HandleGetChineseNames(HttpListenerRequest request, HttpListenerResponse response)
    {
        var entries = request.QueryString.GetValues("entry");
        if (entries is not { Length: >= 1 and <= 24 })
        {
            SendError(response, 400, "Provide 1–24 entry parameters as kind:id.");
            return;
        }

        var references = new List<(string Kind, string Id)>();
        foreach (string entry in entries)
        {
            int separator = entry.IndexOf(':');
            if (separator <= 0)
            {
                SendError(response, 400, "Provide each entry as kind:id.");
                return;
            }
            string kind = entry[..separator];
            string id = entry[(separator + 1)..].Trim();
            if (!GameNameLocalization.Supports(kind) || id.Length is < 1 or > 100)
            {
                SendError(response, 400, "Each entry needs a supported kind and a 1–100 character game ID.");
                return;
            }
            references.Add((kind, id));
        }

        var result = RunOnMainThread(() => references.Select(reference =>
        {
            var (kind, id) = reference;
            var target = kind == "enemy" && CombatManager.Instance.DebugOnlyGetState() is { } combat
                ? ResolveTarget(combat, id)
                : null;
            return new
            {
                kind,
                id,
                name = GameNameLocalization.Find(target?.Monster?.Title) ?? GameNameLocalization.Find(kind, id)
            };
        }).ToArray()).GetAwaiter().GetResult();
        SendJson(response, new { status = "ok", language = GameNameLocalization.SimplifiedChinese, entries = result });
    }
}
