using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Multiplayer;

namespace Sts2Coordination;

public static partial class CoordinationMod
{
    private const string WaitingForHost = "waiting_for_host";
    private const string MostVotes = "most_votes";
    private const string AnyYes = "any_yes";
    private sealed record PollInput(string? Question, Choice[]? Options, int LifetimeSeconds = 25, string Rule = MostVotes);

    private sealed class GroupPoll(PollInput input, Question question, ulong[] voters, CombatId? combatId)
    {
        internal string Id { get; } = Guid.NewGuid().ToString("N");
        internal PollInput Input { get; } = input;
        internal Question Question { get; } = question;
        internal ulong[] Voters { get; } = voters;
        internal CombatId? CombatId { get; } = combatId;
        internal Dictionary<ulong, int?> Responses { get; } = [];
        internal long RoundStarted { get; set; } = Stopwatch.GetTimestamp();
        internal string Status { get; set; } = Pending;
        internal bool IsOpen => Status is Pending or WaitingForHost;
        internal int? Winner { get; set; }
        internal string? Resolution { get; set; }
        internal int[] TiedChoices { get; set; } = [];
        internal Envelope? HostBallot { get; set; }

        internal int[] Tally()
        {
            var counts = new int[Question.Options.Length];
            foreach (int? index in Responses.Values)
                if (index is int choice)
                    counts[choice]++;
            return counts;
        }
    }

    private static GroupPoll? _outgoingPoll;

    private static ulong HostId => _network is NetClientGameService client ? client.HostNetId : _network!.NetId;

    private static bool HasOpenOutgoingCoordination() => _outgoing?.IsOpen == true || _outgoingPoll?.IsOpen == true;

    private static object CreatePoll(PollInput input)
    {
        if (HasOpenOutgoingCoordination())
            return Error("Wait for the current prompt or poll, or cancel it first.", "prompt_pending");
        var voters = _chatRecipientIds;
        if (voters.Length == 0)
            return Error("No connected teammates to poll.", "recipient_unavailable");
        ulong origin = _network!.NetId;
        if (CurrentScope(origin) is not { } scope)
            return Error("Group polls require an active player turn.", "no_combat_turn");
        if (input.Rule == AnyYes)
        {
            if (!IsText(input.Question?.Trim(), 140) || input.Options != null)
                return Error("Provide a yes/no question and omit custom choices for an any_yes poll.");
        }
        else if (input.Rule == MostVotes)
        {
            if (input.Options is not { Length: >= 2 and <= 4 } choices || choices.Any(choice => choice == null))
                return Error("Provide 2–4 poll choices.");
            if (choices.Any(choice => choice.Kind == "teammate_first"))
                return Error("Use a named custom choice for group turn order, such as 'Regent goes first'.");
        }
        else
            return Error("Choose the most_votes or any_yes poll rule.");

        Question QuestionFor(ulong voter)
        {
            var choices = input.Options ?? (UsesChinese(voter)
                ? [new("是"), new("否")]
                : [new("Yes"), new("No")]);
            return BuildQuestion(voter, input.Question, choices, false, input.LifetimeSeconds, scope);
        }
        var poll = new GroupPoll(input, QuestionFor(origin), voters, CombatManager.Instance.CurrentCombatId);
        var ballots = voters.Select(voter => new Envelope("prompt", voter, origin, poll.Id, QuestionFor(voter),
            PollId: poll.Id)).ToArray();
        if (!poll.Question.IsValid || ballots.Any(ballot => !ballot.IsValid))
            return Error("Use a question up to 140 characters and 2–4 choices up to 40 characters, with a lifetime of 5–60 seconds.");
        if (!IsPollCurrent(poll))
            return Error("The combat changed before the poll could be sent.", "prompt_stale");

        foreach (var ballot in ballots)
            if (!Transmit(ballot))
                poll.Responses.Add(ballot.To, null);
        if (poll.Responses.Count == voters.Length)
            return Error("The poll could not be sent to any teammate.", "recipient_unavailable");

        _outgoingPoll = poll;
        return new { status = "ok", outgoing_poll = DescribePoll(poll) };
    }

    private static bool IsPollCurrent(GroupPoll poll) =>
        CombatManager.Instance.IsCurrentLiveCombat(poll.CombatId) &&
        CurrentScope(_network!.NetId) == poll.Question.Scope &&
        poll.Question.Options.All(option => option.CombatTargetId is not uint id || IsTargetAlive(id));

    private static void TickPoll()
    {
        if (_outgoingPoll is not { } poll || poll.Status is Cancelled or Expired)
            return;
        if (!IsPollCurrent(poll))
        {
            StopPoll(poll, Expired);
            return;
        }
        bool timedOut = Stopwatch.GetElapsedTime(poll.RoundStarted) >= TimeSpan.FromSeconds(poll.Input.LifetimeSeconds);
        if (poll.Status == Pending)
        {
            foreach (ulong voter in poll.Voters)
                if (timedOut || !IsConnected(voter))
                {
                    if (poll.Responses.TryAdd(voter, null) && IsConnected(voter))
                        Transmit(new("cancel", voter, PromptId: poll.Id));
                }
            if (poll.Responses.Count == poll.Voters.Length)
                ResolvePoll(poll);
        }
        else if (poll.Status == WaitingForHost)
        {
            if (timedOut || !IsConnected(HostId))
                StopPoll(poll, Expired);
            else
                ShowLocalTiebreak(poll);
        }
    }

    private static void ResolvePoll(GroupPoll poll)
    {
        int[] counts = poll.Tally();
        if (poll.Input.Rule == AnyYes)
        {
            if (counts[1] == poll.Voters.Length)
                SetPollWinner(poll, 1, "all_no");
            else
                StopPoll(poll, Expired);
            return;
        }
        int maximum = counts.Max();
        if (maximum == 0)
        {
            StopPoll(poll, Expired);
            return;
        }
        int[] leaders = Enumerable.Range(0, counts.Length).Where(index => counts[index] == maximum).ToArray();
        if (leaders.Length == 1)
        {
            SetPollWinner(poll, leaders[0], "plurality");
            return;
        }

        ulong host = HostId;
        ulong origin = _network!.NetId;
        poll.TiedChoices = leaders;
        poll.Status = WaitingForHost;
        poll.RoundStarted = Stopwatch.GetTimestamp();
        var choices = leaders.Select(index => poll.Input.Options![index]).ToArray();
        string question = UsesChinese(host) ? "投票结果平票。请房主选择。" : "The poll is tied. Host, choose the winner.";
        var ballot = BuildQuestion(host, question, choices, false, poll.Input.LifetimeSeconds, poll.Question.Scope);
        poll.HostBallot = new("prompt", host, origin, Guid.NewGuid().ToString("N"), ballot,
            PollId: poll.Id, HostTiebreak: true);
        if (host == origin)
            ShowLocalTiebreak(poll);
        else if (!Transmit(poll.HostBallot))
            StopPoll(poll, Expired);
    }

    private static bool IsLocalTiebreak(GroupPoll poll, string ballotId) =>
        poll.HostBallot?.PromptId == ballotId && HostId == _network!.NetId;

    private static void ShowLocalTiebreak(GroupPoll poll)
    {
        if (HostId != _network!.NetId || _incoming is { IsOpen: true } || poll.HostBallot is not { } ballot)
            return;
        var prompt = new Prompt(ballot, poll.CombatId);
        _incoming = prompt;
        ShowIncoming(prompt);
    }

    private static bool TryCompleteLocalTiebreak(Prompt prompt, Reply reply)
    {
        if (_outgoingPoll is not { Status: WaitingForHost } poll || !IsLocalTiebreak(poll, prompt.Id) ||
            reply.ChoiceIndex is not int index)
            return false;
        ApplyReply(prompt, reply);
        SetPollWinner(poll, poll.TiedChoices[index], "host_tiebreak");
        return true;
    }

    private static bool ReceivePollReply(Envelope envelope)
    {
        if (_outgoingPoll is not { IsOpen: true } poll)
            return false;
        if (poll.Status == Pending && envelope.PromptId == poll.Id && poll.Voters.Contains(envelope.From))
        {
            if (envelope.Reply!.ChoiceIndex is int index && index < poll.Question.Options.Length &&
                poll.Responses.TryAdd(envelope.From, index) && poll.Input.Rule == AnyYes && index == 0)
            {
                CloseRemainingBallots(poll);
                SetPollWinner(poll, 0, "any_yes");
            }
            return true;
        }
        if (poll.Status == WaitingForHost && poll.HostBallot?.PromptId == envelope.PromptId &&
            envelope.From == HostId)
        {
            if (envelope.Reply!.ChoiceIndex is int index && index < poll.TiedChoices.Length)
                SetPollWinner(poll, poll.TiedChoices[index], "host_tiebreak");
            return true;
        }
        return false;
    }

    private static void ReceivePollDecline(Envelope envelope)
    {
        if (_outgoingPoll is not { IsOpen: true } poll)
            return;
        if (poll.Status == Pending && envelope.PromptId == poll.Id && poll.Voters.Contains(envelope.From))
            poll.Responses.TryAdd(envelope.From, null);
        else if (poll.Status == WaitingForHost && envelope.PromptId == poll.HostBallot?.PromptId &&
            envelope.From == HostId)
            StopPoll(poll, Expired);
    }

    private static void SetPollWinner(GroupPoll poll, int winner, string resolution)
    {
        poll.Winner = winner;
        poll.Resolution = resolution;
        poll.Status = Answered;
    }

    private static void StopPoll(GroupPoll poll, string status)
    {
        if (poll.Status == Pending)
            CloseRemainingBallots(poll);
        if (poll.Status == WaitingForHost && poll.HostBallot is { PromptId: { } ballotId } ballot)
        {
            if (_incoming is { IsOpen: true } incoming && IsLocalTiebreak(poll, incoming.Id))
                Finish(incoming, status);
            else if (ballot.To != _network!.NetId)
                Transmit(new("cancel", ballot.To, PromptId: ballotId));
        }
        poll.Status = status;
    }

    private static void CloseRemainingBallots(GroupPoll poll)
    {
        foreach (ulong voter in poll.Voters)
            if (poll.Responses.TryAdd(voter, null))
                Transmit(new("cancel", voter, PromptId: poll.Id));
    }

    private static object DescribePoll(GroupPoll poll) => new
    {
        poll_id = poll.Id,
        status = poll.Status,
        rule = poll.Input.Rule,
        question = poll.Question.Text,
        options = poll.Question.Options.Select(choice => new { choice.Label, choice.Kind, choice.Target }).ToArray(),
        host_slot = FindSlot(HostId),
        vote_counts = poll.Tally(),
        votes = poll.Voters.Select(voter =>
        {
            bool closed = poll.Responses.TryGetValue(voter, out int? index);
            string status = index.HasValue ? "voted" : closed ? "no_vote" : "pending";
            return new { player_slot = FindSlot(voter), choice_index = index, status };
        }).ToArray(),
        tied_choice_indices = poll.TiedChoices,
        winning_choice_index = poll.Winner,
        resolution = poll.Resolution
    };
}
