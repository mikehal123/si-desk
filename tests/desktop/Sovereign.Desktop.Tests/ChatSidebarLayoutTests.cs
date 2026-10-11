using System.Globalization;
using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class ChatSidebarLayoutTests
{
    [Fact]
    public void Empty_chats_show_no_messages_instead_of_a_message_time()
    {
        var chats = new[] { new ChatSidebarChat("empty", "New conversation", Now, null, false), new ChatSidebarChat("sent", "Sent question", Now, null, true) };
        var rows = ChatSidebarLayout.Build(chats, [], Now, Zone, DayOfWeek.Sunday, Format).Recents.Single().Rows;
        Assert.Equal("No messages yet", rows.Single(row => row.Chat.Id == "empty").Timestamp);
        Assert.NotEqual("No messages yet", rows.Single(row => row.Chat.Id == "sent").Timestamp);
    }
    private static readonly IFormatProvider Format = CultureInfo.GetCultureInfo("en-US");
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 22, 10, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(7, 15, 0, ChatActivityGroup.Today, "3:00 PM")]
    [InlineData(6, 18, 0, ChatActivityGroup.Yesterday, "Yesterday")]
    [InlineData(5, 12, 0, ChatActivityGroup.ThisWeek, "10/5/2026")]
    [InlineData(4, 9, 0, ChatActivityGroup.ThisWeek, "10/4/2026")]
    [InlineData(3, 9, 0, ChatActivityGroup.Older, "10/3/2026")]
    public void Describe_buckets_october_2026_around_wednesday(int day, int hour, int minute, ChatActivityGroup group, string text)
    {
        var stamp = Describe(At(day, hour, minute));
        Assert.Equal(group, stamp.Group);
        Assert.Equal(text, stamp.Text);
    }

    [Fact]
    public void Describe_treats_a_later_time_on_the_local_date_as_today()
    {
        var stamp = Describe(At(7, 23, 30));
        Assert.Equal(ChatActivityGroup.Today, stamp.Group);
        Assert.Equal("11:30 PM", stamp.Text);
    }

    [Fact]
    public void Describe_uses_the_supplied_zone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Minus7", TimeSpan.FromHours(-7), "Minus7", "Minus7");
        var stamp = ChatActivityFormat.Describe(At(7, 6), Now, zone, DayOfWeek.Sunday, Format);
        Assert.Equal(ChatActivityGroup.Yesterday, stamp.Group);
        Assert.Equal("Yesterday", stamp.Text);
    }

    [Fact]
    public void Group_titles_are_today_yesterday_this_week_and_older()
    {
        Assert.Equal("Today", ChatActivityFormat.GroupTitle(ChatActivityGroup.Today));
        Assert.Equal("Yesterday", ChatActivityFormat.GroupTitle(ChatActivityGroup.Yesterday));
        Assert.Equal("This week", ChatActivityFormat.GroupTitle(ChatActivityGroup.ThisWeek));
        Assert.Equal("Older", ChatActivityFormat.GroupTitle(ChatActivityGroup.Older));
    }

    [Fact]
    public void Build_orders_today_chats_by_updated_then_id()
    {
        var rows = Assert.Single(Build([
            Chat("b", At(7, 16)),
            Chat("c", At(7, 15)),
            Chat("a", At(7, 16))
        ], []).Recents).Rows;
        Assert.Equal(["a", "b", "c"], rows.Select(row => row.Chat.Id).ToArray());
        Assert.Equal(["4:00 PM", "4:00 PM", "3:00 PM"], rows.Select(row => row.Timestamp).ToArray());
    }

    [Fact]
    public void Build_omits_empty_recents_groups()
    {
        var olderOnly = Build([Chat("old", At(3, 9))], []);
        var older = Assert.Single(olderOnly.Recents);
        Assert.Equal(ChatActivityGroup.Older, older.Group);
        Assert.Equal("Older", older.Title);

        var mixed = Build([Chat("today", At(7, 15)), Chat("old", At(3, 9))], []);
        Assert.Equal([ChatActivityGroup.Today, ChatActivityGroup.Older], mixed.Recents.Select(group => group.Group).ToArray());
        Assert.Equal(["Today", "Older"], mixed.Recents.Select(group => group.Title).ToArray());
    }

    [Fact]
    public void Build_lists_projects_that_have_no_chats()
    {
        var project = Project("p", "Empty", At(1, 0));
        var model = Build([], [project]);
        var section = Assert.Single(model.Projects);
        Assert.Equal(project, section.Project);
        Assert.Empty(section.Rows);
        Assert.Empty(model.Recents);
    }

    [Fact]
    public void Build_places_project_chats_under_the_project_newest_first()
    {
        var project = Project("p", "Alpha", At(1, 0));
        var model = Build([
            Chat("old", At(7, 12), "p"),
            Chat("new", At(7, 18), "p"),
            Chat("loose", At(7, 20))
        ], [project]);
        Assert.Equal(["new", "old"], Assert.Single(model.Projects).Rows.Select(row => row.Chat.Id).ToArray());
        Assert.Equal(["6:00 PM", "12:00 PM"], Assert.Single(model.Projects).Rows.Select(row => row.Timestamp).ToArray());
        Assert.Equal(["loose"], Assert.Single(model.Recents).Rows.Select(row => row.Chat.Id).ToArray());
    }

    [Fact]
    public void Build_sends_null_and_unknown_project_ids_to_recents()
    {
        var model = Build([
            Chat("missing", At(7, 12)),
            Chat("unknown", At(7, 11), "nope")
        ], [Project("p", "Alpha", At(1, 0))]);
        Assert.Empty(Assert.Single(model.Projects).Rows);
        Assert.Equal(["missing", "unknown"], Assert.Single(model.Recents).Rows.Select(row => row.Chat.Id).ToArray());
    }

    [Fact]
    public void Build_orders_projects_by_created_then_id()
    {
        var model = Build([], [
            Project("a", "A", At(2, 0)),
            Project("z", "Z", At(1, 0)),
            Project("b", "B", At(1, 0))
        ]);
        Assert.Equal(["b", "z", "a"], model.Projects.Select(section => section.Project.Id).ToArray());
    }

    [Fact]
    public void Build_includes_rows_when_the_project_is_collapsed()
    {
        var project = Project("p", "Alpha", At(1, 0), collapsed: true);
        var model = Build([Chat("c", At(7, 15), "p")], [project]);
        var section = Assert.Single(model.Projects);
        Assert.True(section.Project.Collapsed);
        Assert.Equal("c", Assert.Single(section.Rows).Chat.Id);
        Assert.Empty(model.Recents);
    }

    [Fact]
    public void Sunday_week_start_classifies_the_previous_day_as_yesterday()
    {
        var sunday = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var stamp = ChatActivityFormat.Describe(At(3, 9), sunday, Zone, DayOfWeek.Sunday, Format);
        Assert.Equal(ChatActivityGroup.Yesterday, stamp.Group);
        Assert.Equal("Yesterday", stamp.Text);
        var model = Build([Chat("y", At(3, 9))], [], sunday);
        Assert.Equal(ChatActivityGroup.Yesterday, Assert.Single(model.Recents).Group);
        Assert.DoesNotContain(model.Recents, group => group.Group == ChatActivityGroup.ThisWeek);
    }

    [Fact]
    public void Build_does_not_reorder_the_input_lists()
    {
        var chats = new List<ChatSidebarChat> { Chat("b", At(7, 12)), Chat("a", At(7, 18)) };
        var projects = new List<ChatSidebarProject> { Project("b", "B", At(2, 0)), Project("a", "A", At(1, 0)) };
        Build(chats, projects);
        Assert.Equal(["b", "a"], chats.Select(chat => chat.Id).ToArray());
        Assert.Equal(["b", "a"], projects.Select(project => project.Id).ToArray());
    }

    private static ChatSidebarModel Build(IReadOnlyList<ChatSidebarChat> chats, IReadOnlyList<ChatSidebarProject> projects, DateTimeOffset? now = null) =>
        ChatSidebarLayout.Build(chats, projects, now ?? Now, Zone, DayOfWeek.Sunday, Format);

    private static ChatActivityStamp Describe(DateTimeOffset updated) =>
        ChatActivityFormat.Describe(updated, Now, Zone, DayOfWeek.Sunday, Format);

    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

    private static ChatSidebarChat Chat(string id, DateTimeOffset updated, string? projectId = null) => new(id, id, updated, projectId);

    private static ChatSidebarProject Project(string id, string name, DateTimeOffset created, bool collapsed = false) => new(id, name, created, collapsed);
}
