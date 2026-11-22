using System;
using System.Collections.Generic;
using Godot;
using Sts2Localization;

namespace Sts2Coordination;

internal partial class CoordinationPanel : CanvasLayer
{
    private const float PanelWidth = 420;
    private const float PanelPadding = 10;
    private const float ChatHistoryHeight = 224;
    private const int MessageLineLimit = 6;
    private static readonly Color TextColor = new("#f7f0e4");
    private static readonly Color AccentColor = new("#e3bc7d");
    private static readonly Color BorderColor = new("#786652");

    internal event Action<string, int>? ChoiceSelected;
    internal event Action<string, string>? TextReplied;
    internal event Action<string>? GoAheadSelected;
    internal event Action<string>? PromptDismissed;
    internal event Func<ulong?, string, bool>? ChatSent;

    private PanelContainer? _card;
    private Button _chatToggle = null!;
    private VBoxContainer _chat = null!;
    private ScrollContainer _chatHistory = null!;
    private VBoxContainer _historyRows = null!;
    private Label _emptyHistory = null!;
    private readonly Queue<ChatHistoryRow> _history = new();
    private bool _historyScrollPending;
    private OptionButton _recipient = null!;
    private HBoxContainer _chatInput = null!;
    private LineEdit _chatText = null!;
    private Button _sendChat = null!;
    private Label _chatError = null!;
    private IReadOnlyList<(ulong Id, string Name)> _recipients = [];
    private VBoxContainer _prompt = null!;
    private Label _sender = null!;
    private Button _dismiss = null!;
    private Label _question = null!;
    private VBoxContainer _choices = null!;
    private Button _replyToggle = null!;
    private Button _goAhead = null!;
    private HBoxContainer _composer = null!;
    private LineEdit _replyText = null!;
    private Button _sendReply = null!;
    private Label _message = null!;
    private Timer _messageTimer = null!;
    private string? _promptId;
    private bool _zh;
    private string EveryoneLabel => _zh ? "所有队友" : "Everyone";

    private sealed record ChatHistoryRow(VBoxContainer Node, Label Author, string? Sender, string? Recipient, bool Broadcast);

    internal void Initialize() => BuildPanel();

    internal void SetLanguage(string language)
    {
        _zh = language == GameNameLocalization.SimplifiedChinese;
        _chatToggle.Text = _zh ? "聊天" : "Chat";
        _chatToggle.TooltipText = _zh ? "查看聊天记录或发消息" : "Read chat history or send a message";
        _emptyHistory.Text = _zh ? "还没有消息。" : "No messages yet.";
        _recipient.TooltipText = _zh ? "选择队友" : "Choose a teammate";
        _chatText.PlaceholderText = _zh ? "给队友发消息" : "Message your teammate";
        _sendChat.Text = _sendReply.Text = _zh ? "发送" : "Send";
        _chatError.Text = _zh ? "发送失败，请重试。" : "Could not send. Try again.";
        _dismiss.TooltipText = _zh ? "关闭提问" : "Dismiss prompt";
        _replyToggle.Text = _zh ? "输入回复" : "Type a reply";
        _goAhead.Text = _zh ? "轮到你了" : "Go ahead";
        _replyText.PlaceholderText = _zh ? "回复队友" : "Reply to your teammate";
        if (_recipients.Count > 1)
            _recipient.SetItemText(_recipients.Count, EveryoneLabel);
        foreach (var row in _history)
            row.Author.Text = FormatChatAuthor(row);
    }

    internal void SetChatRecipients(IReadOnlyList<(ulong Id, string Name)> recipients)
    {
        BuildPanel();
        int previous = _recipient.Selected;
        bool selectedEveryone = _recipients.Count > 1 && previous == _recipients.Count;
        ulong? selectedId = previous >= 0 && previous < _recipients.Count ? _recipients[previous].Id : null;
        _recipient.Clear();
        _recipients = recipients;
        foreach (var (_, name) in recipients)
            _recipient.AddItem(name);
        if (recipients.Count > 1)
            _recipient.AddItem(EveryoneLabel);
        _chatToggle.Visible = recipients.Count > 0 || _history.Count > 0;
        int selectedIndex = 0;
        if (selectedId.HasValue)
        {
            while (selectedIndex < recipients.Count && recipients[selectedIndex].Id != selectedId)
                selectedIndex++;
        }
        if (selectedIndex == recipients.Count)
        {
            if (_chatToggle.Visible)
                ResetChatInput();
            else
                CloseChat();
            selectedIndex = 0;
        }
        if (recipients.Count > 0)
            _recipient.Select(selectedEveryone && recipients.Count > 1 ? recipients.Count : selectedIndex);
        _recipient.Visible = recipients.Count > 1;
        _chatInput.Visible = recipients.Count > 0;
        UpdateVisibility();
    }

    internal void ShowPrompt(string promptId, string senderName, string question, IReadOnlyList<string> choices,
        bool allowText, bool canGoAhead = false)
    {
        BuildPanel();
        _promptId = promptId;
        _sender.Text = senderName;
        _question.Text = question;

        foreach (Node oldChoice in _choices.GetChildren())
        {
            _choices.RemoveChild(oldChoice);
            oldChoice.QueueFree();
        }

        for (int i = 0; i < choices.Count && !canGoAhead; i++)
        {
            int index = i;
            var choice = new Button
            {
                Text = choices[index],
                TooltipText = choices[index],
                CustomMinimumSize = new Vector2(0, 38),
                Alignment = HorizontalAlignment.Left,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
            };
            choice.Pressed += () =>
            {
                if (_promptId == promptId)
                    ChoiceSelected?.Invoke(promptId, index);
            };
            _choices.AddChild(choice);
        }

        CloseComposer();
        _replyToggle.Visible = allowText && !canGoAhead;
        _goAhead.Visible = canGoAhead;
        _prompt.Show();
        UpdateVisibility();
    }

    internal void ClearPrompt(string promptId)
    {
        if (_promptId != promptId)
            return;

        _promptId = null;
        CloseComposer();
        _prompt.Hide();
        UpdateVisibility();
    }

    internal void AddChatMessage(string? sender, string? recipient, string text, bool broadcast)
    {
        bool follow = _chat.Visible && (_historyScrollPending || IsAtHistoryEnd());
        var node = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        node.AddThemeConstantOverride("separation", 2);
        var author = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        author.AddThemeColorOverride("font_color", AccentColor);
        author.AddThemeFontSizeOverride("font_size", 16);
        var row = new ChatHistoryRow(node, author, sender, recipient, broadcast);
        author.Text = FormatChatAuthor(row);
        node.AddChild(author);
        node.AddChild(new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart });

        if (_history.Count == CoordinationMod.RetainedEvents)
        {
            var oldest = _history.Dequeue().Node;
            int removedHeight = (int)Mathf.Ceil(oldest.Size.Y) + _historyRows.GetThemeConstant("separation");
            _historyRows.RemoveChild(oldest);
            oldest.QueueFree();
            if (_chat.Visible && !follow)
                _chatHistory.ScrollVertical = Math.Max(0, _chatHistory.ScrollVertical - removedHeight);
        }
        _emptyHistory.Hide();
        _history.Enqueue(row);
        _historyRows.AddChild(node);

        if (!_chat.Visible)
            ShowMessage(author.Text, text);
        else if (follow)
            ScrollHistoryToEnd();
    }

    internal void ClearChatHistory()
    {
        while (_history.TryDequeue(out var row))
        {
            _historyRows.RemoveChild(row.Node);
            row.Node.QueueFree();
        }
        _emptyHistory.Show();
        _chatHistory.ScrollVertical = 0;
        _messageTimer.Stop();
        _message.Hide();
        UpdateVisibility();
    }

    private string FormatChatAuthor(ChatHistoryRow row)
    {
        string sender = row.Sender ?? (_zh ? "你" : "You");
        if (row.Broadcast)
            return sender + (_zh ? " · 全队" : " · Party");
        return row.Recipient == null ? sender + (_zh ? " · 私聊" : " · Direct") : $"{sender} → {row.Recipient}";
    }

    private bool IsAtHistoryEnd()
    {
        var scroll = _chatHistory.GetVScrollBar();
        return scroll.MaxValue - scroll.Page - scroll.Value <= 8;
    }

    private async void ScrollHistoryToEnd()
    {
        if (_historyScrollPending || !IsInsideTree())
            return;
        _historyScrollPending = true;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        _historyScrollPending = false;
        if (_chat.Visible)
            _chatHistory.ScrollVertical = (int)_chatHistory.GetVScrollBar().MaxValue;
    }

    internal void ShowMessage(string senderName, string text)
    {
        BuildPanel();
        _message.Text = _zh ? $"{senderName}：{text}" : $"{senderName}: {text}";
        _message.TooltipText = _message.Text;
        var font = _message.GetThemeFont("font");
        int fontSize = _message.GetThemeFontSize("font_size");
        float textHeight = font.GetMultilineStringSize(_message.Text,
            width: PanelWidth - 2 * PanelPadding, fontSize: fontSize, maxLines: MessageLineLimit,
            brkFlags: TextServer.LineBreakFlag.Mandatory | TextServer.LineBreakFlag.WordBound |
                      TextServer.LineBreakFlag.Adaptive).Y;
        int lineSpacing = _message.GetThemeConstant("line_spacing");
        _message.CustomMinimumSize = new Vector2(0,
            Mathf.Ceil(Mathf.Max(textHeight, font.GetHeight(fontSize))) +
            Mathf.Max(0, lineSpacing) * (MessageLineLimit - 1));
        _message.Show();
        UpdateVisibility();
        if (IsInsideTree())
            _messageTimer.Start();
    }

    private void BuildPanel()
    {
        if (_card != null)
            return;

        Layer = 20;
        var theme = CreateTheme();
        _chatToggle = new Button { Theme = theme };
        _chatToggle.Pressed += ToggleChat;
        AddChild(_chatToggle);
        _chatToggle.AnchorLeft = _chatToggle.AnchorRight = 1;
        _chatToggle.OffsetLeft = -110;
        _chatToggle.OffsetRight = -22;
        _chatToggle.OffsetTop = 96;
        _chatToggle.OffsetBottom = 136;
        _chatToggle.Hide();

        _card = new PanelContainer { Theme = theme, MouseFilter = Control.MouseFilterEnum.Ignore };
        AddChild(_card);
        _card.AnchorLeft = 1;
        _card.AnchorRight = 1;
        _card.OffsetLeft = -(PanelWidth + 22);
        _card.OffsetRight = -22;
        _card.OffsetTop = 144;
        _card.OffsetBottom = 144;

        var content = new VBoxContainer();
        _card.AddChild(content);

        _chat = new VBoxContainer();
        content.AddChild(_chat);
        _chatHistory = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, ChatHistoryHeight),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };
        _chat.AddChild(_chatHistory);
        _historyRows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _chatHistory.AddChild(_historyRows);
        _emptyHistory = new Label();
        _historyRows.AddChild(_emptyHistory);
        _recipient = new OptionButton
        {
            ClipText = true,
            FitToLongestItem = false,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        _chat.AddChild(_recipient);
        _chatInput = new HBoxContainer();
        _chat.AddChild(_chatInput);
        _chatText = new LineEdit
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MaxLength = 200
        };
        _chatText.TextSubmitted += _ => SendChat();
        _chatText.TextChanged += text =>
        {
            _sendChat.Disabled = string.IsNullOrWhiteSpace(text);
            _chatError.Hide();
        };
        _chatInput.AddChild(_chatText);
        _sendChat = new Button { Disabled = true };
        _sendChat.Pressed += SendChat;
        _chatInput.AddChild(_sendChat);
        _chatError = new Label();
        _chat.AddChild(_chatError);
        _chatError.Hide();
        _chat.Hide();

        _prompt = new VBoxContainer();
        content.AddChild(_prompt);
        var header = new HBoxContainer();
        _prompt.AddChild(header);
        _sender = new Label
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        _sender.AddThemeColorOverride("font_color", AccentColor);
        header.AddChild(_sender);
        _dismiss = new Button { Text = "×", CustomMinimumSize = new Vector2(35, 32) };
        _dismiss.Pressed += DismissPrompt;
        header.AddChild(_dismiss);

        _question = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _question.AddThemeFontSizeOverride("font_size", 22);
        _prompt.AddChild(_question);
        _choices = new VBoxContainer();
        _prompt.AddChild(_choices);

        var replyActions = new HBoxContainer();
        _prompt.AddChild(replyActions);
        _replyToggle = new Button();
        _replyToggle.Pressed += () =>
        {
            if (_composer.Visible)
                CloseComposer();
            else
            {
                _composer.Show();
                _replyText.GrabFocus();
            }
        };
        replyActions.AddChild(_replyToggle);
        _goAhead = new Button();
        _goAhead.Pressed += () =>
        {
            if (_promptId is { } promptId)
                GoAheadSelected?.Invoke(promptId);
        };
        replyActions.AddChild(_goAhead);

        _composer = new HBoxContainer();
        _prompt.AddChild(_composer);
        _replyText = new LineEdit
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MaxLength = 200
        };
        _replyText.TextSubmitted += _ => SendReply();
        _replyText.TextChanged += text => _sendReply.Disabled = string.IsNullOrWhiteSpace(text);
        _composer.AddChild(_replyText);
        _sendReply = new Button { Disabled = true };
        _sendReply.Pressed += SendReply;
        _composer.AddChild(_sendReply);

        _message = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Pass,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MaxLinesVisible = MessageLineLimit,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        content.AddChild(_message);

        _messageTimer = new Timer { OneShot = true, WaitTime = 12 };
        _messageTimer.Timeout += () =>
        {
            _message.Hide();
            UpdateVisibility();
        };
        AddChild(_messageTimer);
        _composer.Hide();
        _message.Hide();
        _prompt.Hide();
        _card.MinimumSizeChanged += FitCard;
        SetLanguage("eng");
        UpdateVisibility();
    }

    private void SendReply()
    {
        string text = _replyText.Text.Trim();
        if (_promptId is { } promptId && text.Length > 0)
        {
            CloseComposer();
            TextReplied?.Invoke(promptId, text);
        }
    }

    private void ToggleChat()
    {
        if (_chat.Visible)
        {
            if (_chatText.HasFocus())
                _chatText.ReleaseFocus();
            _chat.Hide();
            UpdateVisibility();
            return;
        }
        _messageTimer.Stop();
        _message.Hide();
        _chat.Show();
        UpdateVisibility();
        ScrollHistoryToEnd();
        if (_recipients.Count > 0)
            _chatText.GrabFocus();
    }

    private void SendChat()
    {
        string text = _chatText.Text.Trim();
        int index = _recipient.Selected;
        if (text.Length == 0 || index < 0 || index >= _recipient.ItemCount)
            return;
        ulong? recipient = index < _recipients.Count ? _recipients[index].Id : null;
        if (ChatSent?.Invoke(recipient, text) != true)
        {
            _chatError.Show();
            _chatText.GrabFocus();
            return;
        }
        ResetChatInput();
        _chatText.GrabFocus();
        UpdateVisibility();
    }

    private void CloseChat()
    {
        if (_chatText.HasFocus())
            _chatText.ReleaseFocus();
        ResetChatInput();
        _chat.Hide();
    }

    private void ResetChatInput()
    {
        _chatText.Clear();
        _sendChat.Disabled = true;
        _chatError.Hide();
    }

    private void DismissPrompt()
    {
        if (_promptId is not { } promptId)
            return;

        ClearPrompt(promptId);
        PromptDismissed?.Invoke(promptId);
    }

    private void CloseComposer()
    {
        if (_replyText.HasFocus())
            _replyText.ReleaseFocus();
        _replyText.Clear();
        _sendReply.Disabled = true;
        _composer.Hide();
    }

    private void UpdateVisibility()
    {
        _card!.Visible = _chat.Visible || _prompt.Visible || _message.Visible;
        FitCard();
    }

    // Wrapped labels may report a different height after the container lays them out.
    private void FitCard() => _card!.SetDeferred(Control.PropertyName.Size, new Vector2(PanelWidth, 0));

    private static Theme CreateTheme()
    {
        var theme = new Theme { DefaultFontSize = 20 };
        if (ResourceLoader.Load<Font>("res://fonts/zhs/NotoSansMonoCJKsc-Regular.otf") is { } chineseFont)
            theme.DefaultFont = new FontVariation { BaseFont = ThemeDB.FallbackFont, Fallbacks = [chineseFont] };
        theme.SetColor("font_color", "Label", TextColor);
        theme.SetColor("font_color", "Button", TextColor);
        theme.SetColor("font_hover_color", "Button", TextColor);
        theme.SetColor("font_pressed_color", "Button", TextColor);
        theme.SetColor("font_focus_color", "Button", TextColor);
        theme.SetColor("font_disabled_color", "Button", new Color("#a5aaa9"));
        theme.SetColor("font_color", "LineEdit", TextColor);
        theme.SetColor("caret_color", "LineEdit", TextColor);
        theme.SetConstant("separation", "VBoxContainer", 8);
        theme.SetConstant("separation", "HBoxContainer", 8);
        theme.SetStylebox("panel", "PanelContainer", Background(new Color("#182230f7"), BorderColor, 10));
        theme.SetStylebox("normal", "Button", Background(new Color("#2c3946"), BorderColor, 6));
        theme.SetStylebox("hover", "Button", Background(new Color("#3d4c59"), AccentColor, 6));
        theme.SetStylebox("pressed", "Button", Background(new Color("#4e5158"), AccentColor, 6));
        theme.SetStylebox("disabled", "Button", Background(new Color("#242d36"), BorderColor, 6));
        theme.SetStylebox("normal", "LineEdit", Background(new Color("#101721"), BorderColor, 6));
        var focus = Background(Colors.Transparent, AccentColor, 6);
        focus.DrawCenter = false;
        theme.SetStylebox("focus", "Button", focus);
        theme.SetStylebox("focus", "LineEdit", focus);
        return theme;
    }

    private static StyleBoxFlat Background(Color color, Color border, int radius)
    {
        var style = new StyleBoxFlat
        {
            BgColor = color,
            BorderColor = border,
            ContentMarginLeft = PanelPadding,
            ContentMarginRight = PanelPadding,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(radius);
        return style;
    }
}
