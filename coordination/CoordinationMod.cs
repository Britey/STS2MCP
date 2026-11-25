using System.Diagnostics;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using Sts2Localization;

namespace Sts2Coordination;

[ModInitializer(nameof(Initialize))]
public static partial class CoordinationMod
{
    private const string Pending = "pending";
    private const string Waiting = "waiting_for_teammate";
    private const string Answered = "answered";
    private const string Expired = "expired";
    private const string Cancelled = "cancelled";
    private const int RetainedEvents = 40;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record PromptInput(int RecipientSlot, string? Question, Choice[] Options,
        bool AllowText = true, int LifetimeSeconds = 25);
    private sealed record StateInput(int AfterId = 0);
    private sealed record ReplyInput(string PromptId, int? ChoiceIndex, string? Text, bool GoAhead);
    private sealed record MessageInput(int? RecipientSlot, string Text, bool AllPlayers = false);
    private sealed record PromptRef(string PromptId, uint? CombatTargetId);

    private sealed class Prompt(Envelope envelope, CombatId? combatId)
    {
        internal Envelope Envelope { get; } = envelope;
        internal CombatId? CombatId { get; } = combatId;
        internal int CompletedPlays { get; set; }
        internal long Started { get; } = Stopwatch.GetTimestamp();
        internal string Status { get; set; } = Pending;
        internal Reply? Reply { get; set; }
        internal string? Release { get; set; }
        internal string Id => Envelope.PromptId!;
        internal Question Question => Envelope.Question!;
        internal bool IsOpen => Status is Pending or Waiting;
        internal Choice? SelectedChoice => Reply?.ChoiceIndex is int index ? Question.Options[index] : null;
    }

    private sealed record MessageEvent(int Id, int? SenderSlot, int? RecipientSlot, string Text, bool IsLocal, bool Broadcast);

    private static CoordinationPanel? _panel;
    private static INetGameService? _network;
    private static RunState? _run;
    private static Prompt? _outgoing;
    private static Prompt? _incoming;
    private static readonly List<ulong> ConnectedTeammates = [];
    private static readonly Dictionary<ulong, string> TeammateLanguages = [];
    private static string _localLanguage = "eng";
    private static string? _partyLanguage;
    private static Func<string?>? _getConfiguredNameLanguage;
    private static ulong[] _chatRecipientIds = [];
    private static readonly Queue<MessageEvent> Messages = new();
    private static int _nextEventId;
    private sealed record PromptProgress(Prompt? Incoming, string? IncomingStatus, Prompt? Outgoing,
        string? OutgoingStatus, GroupPoll? Poll, string? PollStatus, int Votes);
    private static PromptProgress _lastPromptProgress = new(null, null, null, null, null, null, 0);

    private static int GetCoordinationCursor()
    {
        // Ballots are append-only. A status change covers replies, expiry, cancellation and the host tiebreak.
        var progress = new PromptProgress(_incoming, _incoming?.Status, _outgoing, _outgoing?.Status,
            _outgoingPoll, _outgoingPoll?.Status, _outgoingPoll?.Responses.Count ?? 0);
        if (progress != _lastPromptProgress)
        {
            _lastPromptProgress = progress;
            _nextEventId++;
        }
        return _nextEventId;
    }
    private static long _lastTick;

    public static void Initialize()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
            return;
        var panel = new CoordinationPanel();
        panel.Initialize();
        panel.ChoiceSelected += (id, index) => ReplyFromPanel(id, new(ChoiceIndex: index));
        panel.TextReplied += (id, text) => ReplyFromPanel(id, new(Text: text));
        panel.GoAheadSelected += id => ReplyFromPanel(id, new(GoAhead: true));
        panel.PromptDismissed += id => CancelPrompt(id);
        panel.ChatSent += (recipient, text) => recipient is ulong id
            ? TrySendText(id, text)
            : BroadcastText(text).Length > 0;
        _panel = panel;
        tree.Root.CallDeferred(Node.MethodName.AddChild, panel);
        tree.ProcessFrame += OnFrame;
    }

    // Read on the game thread by the MCP bridge.
    public static string? GetPartyNameLanguage()
    {
        var manager = RunManager.Instance;
        return _network is { IsConnected: true } && !manager.IsCleaningUp &&
            ReferenceEquals(manager.NetService, _network) && ReferenceEquals(manager.DebugOnlyGetState(), _run)
            ? (_network is NetHostGameService ? _getConfiguredNameLanguage?.Invoke() : _partyLanguage)
            : null;
    }

    // Called on the main thread by the optional MCP bridge.
    public static string Dispatch(string action, string payload)
    {
        Tick();
        if (_network == null || _run == null)
            return Encode(Error("Coordination requires a connected co-op run.", "no_coop_session"));
        try
        {
            object response = action switch
            {
                "coordination_state" => GetState(Decode<StateInput>(payload).AfterId),
                "coordination_prompt" => CreatePrompt(Decode<PromptInput>(payload)),
                "coordination_poll" => CreatePoll(Decode<PollInput>(payload)),
                "coordination_reply" => SendReply(Decode<ReplyInput>(payload)),
                "coordination_message" => SendText(Decode<MessageInput>(payload)),
                "coordination_cancel" => CancelPrompt(Decode<PromptRef>(payload).PromptId),
                "coordination_validate" => ValidateAnswer(Decode<PromptRef>(payload)),
                _ => Error("Unknown coordination action.")
            };
            return Encode(response);
        }
        catch (JsonException)
        {
            return Encode(Error("Invalid coordination parameters."));
        }
    }

    private static string Encode(object data) => JsonSerializer.Serialize(data, Json);
    private static T Decode<T>(string payload) => JsonSerializer.Deserialize<T>(payload, Json) ??
        throw new JsonException("Missing coordination payload.");

    internal static bool IsText(string? value, int length) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= length && !value.Any(char.IsControl);

    private static object Error(string error, string reason = "invalid_coordination") =>
        new { status = "error", error, reason };

    private static void OnFrame()
    {
        if (Stopwatch.GetElapsedTime(_lastTick) < TimeSpan.FromMilliseconds(100))
            return;
        _lastTick = Stopwatch.GetTimestamp();
        Tick();
    }

    private static void Tick()
    {
        string language = LocManager.Instance?.Language ?? "eng";
        bool languageChanged = !string.Equals(language, _localLanguage, StringComparison.Ordinal);
        if (languageChanged)
        {
            _localLanguage = language;
            _panel?.SetLanguage(language);
        }

        var manager = RunManager.Instance;
        var run = manager.IsInProgress && !manager.IsCleaningUp ? manager.DebugOnlyGetState() : null;
        var network = run != null && manager.NetService is { IsConnected: true } net && net.Type.IsMultiplayer()
            ? net : null;
        if (!ReferenceEquals(network, _network) || !ReferenceEquals(run, _run))
        {
            _network?.UnregisterMessageHandler<CoordinationMessage>(Receive);
            if (_incoming != null)
                _panel?.ClearPrompt(_incoming.Id);
            _incoming = _outgoing = null;
            _outgoingPoll = null;
            _partyLanguage = null;
            Messages.Clear();
            TeammateLanguages.Clear();
            _chatRecipientIds = [];
            _panel?.SetChatRecipients([]);
            _run = run;
            _network = network;
            _network?.RegisterMessageHandler<CoordinationMessage>(Receive);
            _getConfiguredNameLanguage = _network is NetHostGameService
                ? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "STS2_MCP")
                    ?.GetType("STS2_MCP.McpMod")?.GetMethod("GetConfiguredNameLanguage")?.CreateDelegate<Func<string?>>()
                : null;
        }
        if (_network == null)
            return;
        if (_network is NetHostGameService)
        {
            string? current = _getConfiguredNameLanguage?.Invoke();
            languageChanged |= _partyLanguage != current;
            _partyLanguage = current;
        }
        UpdateChatRecipients(_run!, _network.NetId, languageChanged);

        if (_incoming is { IsOpen: true } incoming && !IsCurrent(incoming))
            Finish(incoming, Expired);
        TickPoll();
        if (_outgoing is not { } outgoing || outgoing.Status is Cancelled or Expired)
            return;
        if (!IsCurrent(outgoing))
            Finish(outgoing, Expired);
        else if (outgoing.Status == Waiting &&
            CountCompletedPlays(outgoing.Envelope.To) > outgoing.CompletedPlays)
        {
            Finish(outgoing, Answered, "teammate_played");
            Transmit(new("complete", outgoing.Envelope.To, PromptId: outgoing.Id));
        }
    }

    private static bool IsConnected(ulong playerId) =>
        _network != null && (playerId == _network.NetId ||
            RunManager.Instance.RunLobby?.PlayerIds.Contains(playerId) == true);

    private static void UpdateChatRecipients(RunState run, ulong localId, bool languageChanged)
    {
        ConnectedTeammates.Clear();
        foreach (var player in run.Players)
            if (player.NetId != localId && IsConnected(player.NetId))
                ConnectedTeammates.Add(player.NetId);
        if (!ConnectedTeammates.SequenceEqual(_chatRecipientIds))
        {
            var newcomers = ConnectedTeammates.Except(_chatRecipientIds).ToArray();
            _chatRecipientIds = ConnectedTeammates.ToArray();
            foreach (ulong id in TeammateLanguages.Keys.Except(_chatRecipientIds).ToArray())
                TeammateLanguages.Remove(id);
            foreach (ulong id in newcomers)
                Transmit(LocaleEnvelope("locale_hello", id));
            _panel?.SetChatRecipients(_chatRecipientIds.Select(id => (id, GetName(id))).ToArray());
        }
        if (languageChanged)
            foreach (ulong id in _chatRecipientIds)
                Transmit(LocaleEnvelope("locale", id));
    }

    private static Envelope LocaleEnvelope(string kind, ulong recipient) => new(kind, recipient, Language: _localLanguage,
        PartyLanguage: _network is NetHostGameService ? _partyLanguage ?? "game" : null);

    private static Player? FindPlayer(ulong playerId) => _run?.Players.FirstOrDefault(p => p.NetId == playerId);

    private static int? FindSlot(ulong playerId)
    {
        if (_run == null)
            return null;
        for (int i = 0; i < _run.Players.Count; i++)
            if (_run.Players[i].NetId == playerId)
                return i;
        return null;
    }

    private static bool TryRecipient(int slot, out ulong id)
    {
        id = 0;
        if (_run == null || _network == null || slot < 0 || slot >= _run.Players.Count)
            return false;
        id = _run.Players[slot].NetId;
        return id != _network.NetId && IsConnected(id);
    }

    private static CombatScope? CurrentScope(ulong playerId)
    {
        var combat = CombatManager.Instance;
        if (_run == null || !combat.IsInProgress ||
            combat.DebugOnlyGetState() is not { CurrentSide: CombatSide.Player } state ||
            FindPlayer(playerId) is not { Creature.IsAlive: true, PlayerCombatState: { } player })
            return null;
        return new(_run.CurrentActIndex, _run.TotalFloor, state.RoundNumber, player.TurnNumber);
    }

    private static bool IsTargetAlive(uint id) =>
        CombatManager.Instance.DebugOnlyGetState()?.Enemies.Any(enemy => enemy.CombatId == id && enemy.IsAlive) == true;

    private static bool IsCurrent(Prompt prompt) =>
        IsConnected(prompt.Envelope.From) && IsConnected(prompt.Envelope.To) &&
        CombatManager.Instance.IsCurrentLiveCombat(prompt.CombatId) &&
        CurrentScope(prompt.Envelope.From) == prompt.Question.Scope &&
        (!prompt.IsOpen || Stopwatch.GetElapsedTime(prompt.Started) < TimeSpan.FromSeconds(prompt.Question.LifetimeSeconds)) &&
        prompt.Question.Options.All(option => option.CombatTargetId is not uint id || IsTargetAlive(id));

    private static int CountCompletedPlays(ulong playerId) => CombatManager.Instance.History.CardPlaysFinished
        .Count(entry => entry.CardPlay.Player.NetId == playerId && !entry.CardPlay.IsAutoPlay && entry.CardPlay.IsLastInSeries);

    private static bool Transmit(Envelope envelope)
    {
        if (_network == null || !IsConnected(envelope.To))
            return false;
        envelope = envelope with { From = _network.NetId };
        if (!envelope.IsValid)
            return false;
        var packet = new CoordinationMessage { Json = Encode(envelope) };
        if (packet.Json.Length > CoordinationMessage.MaxLength)
            return false;
        if (_network is NetClientGameService)
            _network.SendMessage(packet);
        else
            _network.SendMessage(packet, envelope.To);
        return true;
    }

    private static void Receive(CoordinationMessage packet, ulong senderId)
    {
        if (_network == null || packet.Json is not { Length: > 0 and <= CoordinationMessage.MaxLength })
            return;
        Envelope envelope;
        try { envelope = Decode<Envelope>(packet.Json); }
        catch (JsonException) { return; }

        if (_network is NetClientGameService client)
        {
            if (senderId != client.HostNetId || envelope.To != _network.NetId)
                return;
        }
        else
        {
            if (envelope.PartyLanguage != null)
                return;
            envelope = envelope with { From = senderId };
        }
        if (!envelope.IsValid || !IsConnected(envelope.From) || !IsConnected(envelope.To))
            return;
        if (envelope.To != _network.NetId)
        {
            _network.SendMessage(new CoordinationMessage { Json = Encode(envelope) }, envelope.To);
            return;
        }

        Tick();
        if (_network == null)
            return;
        switch (envelope.Kind)
        {
            case "locale_hello":
            case "locale":
                TeammateLanguages[envelope.From] = envelope.Language!;
                if (_network is NetClientGameService hostConnection && envelope.From == hostConnection.HostNetId)
                    _partyLanguage = envelope.PartyLanguage is "eng" or "zhs" ? envelope.PartyLanguage : null;
                if (envelope.Kind == "locale_hello")
                    Transmit(LocaleEnvelope("locale", envelope.From));
                break;
            case "prompt":
                AcceptPrompt(envelope);
                break;
            case "reply":
                if (!ReceivePollReply(envelope) && _outgoing is { IsOpen: true } outgoing && outgoing.Id == envelope.PromptId &&
                    outgoing.Envelope.To == envelope.From)
                    ApplyReply(outgoing, envelope.Reply!);
                break;
            case "cancel":
                ReceivePollDecline(envelope);
                if (_outgoing is { IsOpen: true } cancelled && cancelled.Id == envelope.PromptId &&
                    cancelled.Envelope.To == envelope.From)
                    Finish(cancelled, Cancelled);
                if (_incoming is { IsOpen: true } incoming && incoming.Id == envelope.PromptId &&
                    incoming.Envelope.From == envelope.From)
                    Finish(incoming, Cancelled);
                break;
            case "complete":
                if (_incoming is { Status: Waiting } waiting && waiting.Id == envelope.PromptId &&
                    waiting.Envelope.From == envelope.From)
                    Finish(waiting, Answered, "teammate_played");
                break;
            case "message":
                AddMessage(envelope, isLocal: false);
                break;
        }
    }

    private static object CreatePrompt(PromptInput input)
    {
        if (HasOpenOutgoingCoordination())
            return Error("Wait for the current prompt or poll, or cancel it first.", "prompt_pending");
        if (!TryRecipient(input.RecipientSlot, out ulong recipient))
            return Error("Choose a connected teammate by player slot.", "recipient_unavailable");
        ulong origin = _network!.NetId;
        if (CurrentScope(origin) is not { } scope)
            return Error("Quick replies require an active player turn.", "no_combat_turn");
        if (input.Options is not { Length: >= 2 and <= 4 } choices || choices.Any(choice => choice == null))
            return Error("Provide 2–4 coordination choices.");

        var question = BuildQuestion(recipient, input.Question, choices, input.AllowText, input.LifetimeSeconds, scope);
        if (!question.IsValid)
            return Error("Use a question up to 140 characters and 2–4 choices up to 40 characters, with a lifetime of 5–60 seconds.");
        var envelope = new Envelope("prompt", recipient, origin, Guid.NewGuid().ToString("N"), question);
        var prompt = new Prompt(envelope, CombatManager.Instance.CurrentCombatId);
        if (!IsCurrent(prompt) || !Transmit(envelope))
            return Error("The team or combat changed before the prompt could be sent.", "prompt_stale");
        _outgoing = prompt;
        return new { status = "ok", outgoing_prompt = Describe(prompt) };
    }

    private static bool UsesChinese(ulong recipient) =>
        (_partyLanguage ?? TeammateLanguages.GetValueOrDefault(recipient, _localLanguage)) == GameNameLocalization.SimplifiedChinese;

    private static Question BuildQuestion(ulong recipient, string? text, Choice[] choices, bool allowText,
        int lifetimeSeconds, CombatScope scope)
    {
        bool zh = UsesChinese(recipient);
        var options = choices.Select(choice => choice.Label == null
            ? choice with { Label = GetDefaultChoiceLabel(choice, zh) }
            : choice).ToArray();
        string questionText = text?.Trim() ?? (zh ? "这回合怎么配合？" : "How should we coordinate this turn?");
        return new(questionText, options, allowText, lifetimeSeconds, scope);
    }

    private static string? GetDefaultChoiceLabel(Choice choice, bool zh) => choice.Kind switch
    {
        "agent_first" => zh ? "你先出牌" : "You go first",
        "teammate_first" => zh ? "我先出牌" : "I'll go first",
        "no_preference" => zh ? "都可以" : "No preference",
        "focus_enemy" => GetFocusLabel(choice.CombatTargetId, zh),
        _ => null
    };

    private static string? GetFocusLabel(uint? targetId, bool zh)
    {
        if (targetId == null || CombatManager.Instance.DebugOnlyGetState() is not { } combat)
            return null;

        var target = combat.Enemies.FirstOrDefault(enemy => enemy.IsAlive && enemy.CombatId == targetId);
        if (target?.Monster is not { } monster)
            return null;

        string language = zh ? GameNameLocalization.SimplifiedChinese : "eng";
        string name = GameNameLocalization.Find(monster.Title, language) ?? monster.Title.GetFormattedText();
        var matching = combat.Enemies.Where(enemy => enemy.IsAlive && enemy.Monster?.Id == monster.Id).ToList();
        if (matching.Count > 1)
        {
            int index = matching.IndexOf(target) + 1;
            name += zh ? $"（第 {index} 个）" : $" #{index}";
        }
        return zh ? $"集火{name}" : $"Focus {name}";
    }

    private static void AcceptPrompt(Envelope envelope)
    {
        if (_incoming?.Id == envelope.PromptId)
            return;
        var prompt = new Prompt(envelope, CombatManager.Instance.CurrentCombatId);
        if ((_incoming?.IsOpen ?? false) || !IsCurrent(prompt) ||
            (envelope.HostTiebreak && _network!.NetId != HostId))
        {
            Transmit(new("cancel", envelope.From, PromptId: prompt.Id));
            return;
        }
        _incoming = prompt;
        ShowIncoming(prompt);
    }

    private static object SendReply(ReplyInput input)
    {
        if (_incoming is not { IsOpen: true } prompt || prompt.Id != input.PromptId)
            return Error("That prompt is no longer open.", "prompt_stale");
        var reply = new Reply(input.ChoiceIndex, input.Text?.Trim(), input.GoAhead);
        if (!IsReplyAllowed(prompt, reply))
            return Error("Choose one available reply.");
        if (reply.ChoiceIndex is int index && prompt.Question.Options[index].Kind == "teammate_first")
            reply = reply with { CompletedPlays = CountCompletedPlays(prompt.Envelope.To) };
        if (TryCompleteLocalTiebreak(prompt, reply))
            return new { status = "ok", incoming_prompt = Describe(prompt) };
        if (!Transmit(new("reply", prompt.Envelope.From, PromptId: prompt.Id, Reply: reply)))
            return Error("The teammate is no longer connected.", "recipient_unavailable");
        ApplyReply(prompt, reply);
        return new { status = "ok", incoming_prompt = Describe(prompt) };
    }

    private static bool IsReplyAllowed(Prompt prompt, Reply reply)
    {
        if (!reply.IsValid || !IsCurrent(prompt))
            return false;
        if (prompt.Status == Waiting)
            return reply.GoAhead;
        return prompt.Status == Pending &&
            (reply.ChoiceIndex < prompt.Question.Options.Length || (reply.Text != null && prompt.Question.AllowText));
    }

    private static void ApplyReply(Prompt prompt, Reply reply)
    {
        if (!IsReplyAllowed(prompt, reply))
            return;
        if (!reply.GoAhead)
            prompt.Reply = reply;
        if (!reply.GoAhead && prompt.SelectedChoice?.Kind == "teammate_first")
        {
            prompt.Status = Waiting;
            prompt.CompletedPlays = reply.CompletedPlays ?? CountCompletedPlays(prompt.Envelope.To);
            if (ReferenceEquals(_incoming, prompt))
                ShowIncoming(prompt);
            return;
        }
        Finish(prompt, Answered, reply.GoAhead ? "go_ahead" : null);
    }

    private static void ReplyFromPanel(string id, Reply reply)
    {
        Tick();
        if (_network != null)
            SendReply(new(id, reply.ChoiceIndex, reply.Text, reply.GoAhead));
    }

    private static object CancelPrompt(string id)
    {
        if (_outgoingPoll is { IsOpen: true } poll &&
            (poll.Id == id || IsLocalTiebreak(poll, id)))
        {
            StopPoll(poll, Cancelled);
            return new { status = "ok" };
        }
        if (_outgoing is { IsOpen: true } outgoing && outgoing.Id == id)
        {
            Finish(outgoing, Cancelled);
            Transmit(new("cancel", outgoing.Envelope.To, PromptId: id));
            return new { status = "ok" };
        }
        if (_incoming is { IsOpen: true } incoming && incoming.Id == id)
        {
            Finish(incoming, Cancelled);
            Transmit(new("cancel", incoming.Envelope.From, PromptId: id));
            return new { status = "ok" };
        }
        return Error("That prompt is no longer open.", "prompt_stale");
    }

    private static void ShowIncoming(Prompt prompt)
    {
        string sender = GetName(prompt.Envelope.From);
        if (prompt.Envelope.PollId != null)
            sender += UsesChinese(_network!.NetId) ? " · 全队投票" : " · Team poll";
        _panel?.ShowPrompt(prompt.Id, sender, prompt.Question.Text,
            prompt.Question.Options.Select(option => option.Label!).ToArray(),
            prompt.Question.AllowText, prompt.Status == Waiting);
    }

    private static void Finish(Prompt prompt, string status, string? release = null)
    {
        prompt.Status = status;
        prompt.Release = release;
        if (ReferenceEquals(_incoming, prompt))
            _panel?.ClearPrompt(prompt.Id);
    }

    private static object ValidateAnswer(PromptRef request)
    {
        var choice = _outgoingPoll is { Status: Answered, Winner: int winner } poll && poll.Id == request.PromptId
            ? poll.Question.Options[winner]
            : null;
        if (_outgoing is { Status: Answered } prompt && prompt.Id == request.PromptId)
            choice = prompt.SelectedChoice; // Free-text answers may have no selected button.
        else if (choice == null)
            return Error("Wait for a live answer or make a fresh plan.", "coordination_answer_unavailable");
        if (request.CombatTargetId is uint id && choice?.CombatTargetId is uint chosen && id != chosen)
            return Error("This action targets a different enemy from the agreed choice.", "coordination_target_mismatch");
        return new { status = "ok" };
    }

    private static object SendText(MessageInput input)
    {
        if (!IsText(input.Text?.Trim(), 200))
            return Error("Enter a message of up to 200 characters.");
        if (input.AllPlayers)
        {
            if (input.RecipientSlot != null)
                return Error("Choose a recipient slot or send to everyone.");
            var recipients = BroadcastText(input.Text);
            return recipients.Length > 0
                ? new { status = "ok", recipient_slots = recipients.Select(FindSlot).ToArray() }
                : Error("No connected teammates to message.", "recipient_unavailable");
        }
        if (input.RecipientSlot is not int slot || !TryRecipient(slot, out ulong recipient))
            return Error("Choose a connected teammate by player slot.", "recipient_unavailable");
        return TrySendText(recipient, input.Text)
            ? new { status = "ok" }
            : Error("Enter a message of up to 200 characters.");
    }

    private static bool TrySendText(ulong recipient, string? text)
    {
        if (_network == null || recipient == _network.NetId || !IsConnected(recipient))
            return false;
        var message = new Envelope("message", recipient, _network.NetId, Text: text?.Trim());
        if (!message.IsValid || !Transmit(message))
            return false;
        AddMessage(message, isLocal: true);
        return true;
    }

    private static ulong[] BroadcastText(string? text)
    {
        string? message = text?.Trim();
        if (_network == null || !IsText(message, 200))
            return [];
        ulong origin = _network.NetId;
        var recipients = _chatRecipientIds.Where(id =>
            Transmit(new("message", id, origin, Text: message, Broadcast: true))).ToArray();
        if (recipients.Length > 0)
            AddMessage(new("message", recipients[0], origin, Text: message, Broadcast: true), isLocal: true);
        return recipients;
    }

    private static void AddMessage(Envelope envelope, bool isLocal)
    {
        var entry = new MessageEvent(++_nextEventId, FindSlot(envelope.From),
            envelope.Broadcast ? null : FindSlot(envelope.To), envelope.Text!, isLocal, envelope.Broadcast);
        Messages.Enqueue(entry);
        if (Messages.Count > RetainedEvents)
            Messages.Dequeue();
        bool zh = _localLanguage == GameNameLocalization.SimplifiedChinese;
        string sender = isLocal ? (zh ? "你" : "You") : GetName(envelope.From);
        if (envelope.Broadcast)
            sender += zh ? " · 全队" : " · Party";
        _panel?.ShowMessage(sender, entry.Text);
    }

    private static string GetName(ulong id)
    {
        try { return PlatformUtil.GetPlayerName(_network!.Platform, id); }
        catch { return _localLanguage == GameNameLocalization.SimplifiedChinese ? $"玩家 {FindSlot(id) + 1}" : $"Player {FindSlot(id) + 1}"; }
    }

    private static object GetState(int afterId) => new
    {
        status = "ok",
        outgoing_prompt = _outgoing == null ? null : Describe(_outgoing),
        outgoing_poll = _outgoingPoll == null ? null : DescribePoll(_outgoingPoll),
        incoming_prompt = _incoming == null ? null : Describe(_incoming),
        events = Messages.Where(message => message.Id > afterId).ToArray(),
        local_language = _localLanguage,
        party_language = _partyLanguage,
        teammates = _chatRecipientIds.Select(id => new
        {
            player_slot = FindSlot(id),
            name = GetName(id),
            language = TeammateLanguages.GetValueOrDefault(id)
        }).ToArray(),
        latest_id = GetCoordinationCursor()
    };

    private static object Describe(Prompt prompt) => new
    {
        prompt_id = prompt.Id,
        poll_id = prompt.Envelope.PollId,
        host_tiebreak = prompt.Envelope.HostTiebreak,
        status = prompt.Status,
        sender_slot = FindSlot(prompt.Envelope.From),
        recipient_slot = FindSlot(prompt.Envelope.To),
        question = prompt.Question.Text,
        options = prompt.Question.Options.Select(option => new { option.Label, option.Kind, option.Target }).ToArray(),
        allow_text = prompt.Question.AllowText,
        reply = prompt.Reply is { } reply ? new { reply.ChoiceIndex, reply.Text, reply.GoAhead } : null,
        release = prompt.Release
    };
}
