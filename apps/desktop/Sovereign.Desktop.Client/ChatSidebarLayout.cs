namespace Sovereign.Desktop.Client;

public enum ChatActivityGroup { Today, Yesterday, ThisWeek, Older }

public sealed record ChatActivityStamp(string Text, ChatActivityGroup Group);

public static class ChatActivityFormat
{
    public static string GroupTitle(ChatActivityGroup group) => group switch
    {
        ChatActivityGroup.Today => "Today",
        ChatActivityGroup.Yesterday => "Yesterday",
        ChatActivityGroup.ThisWeek => "This week",
        ChatActivityGroup.Older => "Older",
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, null)
    };

    public static ChatActivityStamp Describe(DateTimeOffset updated, DateTimeOffset now, TimeZoneInfo zone, DayOfWeek weekStarts, IFormatProvider format)
    {
        var localUpdated = TimeZoneInfo.ConvertTime(updated, zone);
        var updatedDate = DateOnly.FromDateTime(localUpdated.DateTime);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        if (updatedDate >= today) return new(localUpdated.ToString("t", format), ChatActivityGroup.Today);
        if (updatedDate == today.AddDays(-1)) return new("Yesterday", ChatActivityGroup.Yesterday);
        var weekStart = today.AddDays(-((7 + (today.DayOfWeek - weekStarts)) % 7));
        return new(localUpdated.ToString("d", format), updatedDate >= weekStart ? ChatActivityGroup.ThisWeek : ChatActivityGroup.Older);
    }
}

public sealed record ChatSidebarChat(string Id, string Title, DateTimeOffset Updated, string? ProjectId, bool HasMessages = true);

public sealed record ChatSidebarProject(string Id, string Name, DateTimeOffset Created, bool Collapsed);

public sealed record ChatSidebarRow(ChatSidebarChat Chat, string Timestamp);

public sealed record ChatSidebarGroup(ChatActivityGroup Group, string Title, IReadOnlyList<ChatSidebarRow> Rows);

public sealed record ChatSidebarProjectSection(ChatSidebarProject Project, IReadOnlyList<ChatSidebarRow> Rows);

public sealed record ChatSidebarModel(IReadOnlyList<ChatSidebarProjectSection> Projects, IReadOnlyList<ChatSidebarGroup> Recents);

public static class ChatSidebarLayout
{
    public static ChatSidebarModel Build(IReadOnlyList<ChatSidebarChat> chats, IReadOnlyList<ChatSidebarProject> projects, DateTimeOffset now, TimeZoneInfo zone, DayOfWeek weekStarts, IFormatProvider format)
    {
        var projectIds = new HashSet<string>(projects.Select(project => project.Id), StringComparer.Ordinal);
        var sections = projects
            .OrderBy(project => project.Created)
            .ThenBy(project => project.Id, StringComparer.Ordinal)
            .Select(project => new ChatSidebarProjectSection(project, Rows(chats.Where(chat => chat.ProjectId == project.Id))))
            .ToArray();
        var recent = chats
            .Where(chat => chat.ProjectId is not string projectId || !projectIds.Contains(projectId))
            .Select(Stamp)
            .OrderByDescending(item => item.Chat.Updated)
            .ThenBy(item => item.Chat.Id, StringComparer.Ordinal)
            .ToArray();
        var recents = Enum.GetValues<ChatActivityGroup>()
            .Select(group => new ChatSidebarGroup(group, ChatActivityFormat.GroupTitle(group), recent.Where(item => item.Stamp.Group == group).Select(item => new ChatSidebarRow(item.Chat, item.Chat.HasMessages ? item.Stamp.Text : "No messages yet")).ToArray()))
            .Where(group => group.Rows.Count > 0)
            .ToArray();
        return new(sections, recents);

        (ChatSidebarChat Chat, ChatActivityStamp Stamp) Stamp(ChatSidebarChat chat) =>
            (chat, ChatActivityFormat.Describe(chat.Updated, now, zone, weekStarts, format));

        ChatSidebarRow[] Rows(IEnumerable<ChatSidebarChat> source) => source
            .Select(Stamp)
            .OrderByDescending(item => item.Chat.Updated)
            .ThenBy(item => item.Chat.Id, StringComparer.Ordinal)
            .Select(item => new ChatSidebarRow(item.Chat, item.Chat.HasMessages ? item.Stamp.Text : "No messages yet"))
            .ToArray();
    }
}
