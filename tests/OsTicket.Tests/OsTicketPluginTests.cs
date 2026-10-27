using System.Text.Json;
using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;
using Microsoft.Extensions.Time.Testing;

namespace Brim.OsTicket.Tests;

public sealed class OsTicketPluginTests
{
    private readonly RecordingSink _sink = new();
    private readonly FakeSource _source = new();
    private readonly FakeTimeProvider _time = new(Test.Now);
    private readonly List<string> _opened = [];
    private int _sourcesCreated;

    public OsTicketPluginTests() => Test.Setup();

    private static readonly PluginSettings Configured = new() { Url = "https://support.example.com", Username = "tobias", Password = "geheim" };

    private OsTicketPlugin Create(PluginSettings? settings = null) => new(_sink, _ =>
    {
        _sourcesCreated++;
        return _source;
    }, _opened.Add, _time, settings ?? Configured);

    private static IReadOnlyDictionary<string, IReadOnlyList<TileSize>> Placed(string id, params TileSize[] sizes) =>
        new Dictionary<string, IReadOnlyList<TileSize>> { [id] = sizes };

    private async Task NextRefresh(OsTicketPlugin plugin)
    {
        _time.Advance(plugin.Settings.Refresh);
        await plugin.TickAsync();
    }

    [Fact]
    public async Task Notch_shows_open_and_own_tickets_with_the_list_when_expanded()
    {
        _source.Queues[1] = [Test.T(1043, "Drucker druckt nicht"), Test.T(1040, "Passwort zurücksetzen")];
        _source.Queues[6] = [Test.T(1043, "Drucker druckt nicht")];
        _source.Counts[1] = 12;
        _source.Counts[4] = 0;
        var plugin = Create();

        await plugin.TickAsync();

        var activity = Assert.IsType<ActivityParams>(_sink.Last);
        Assert.Equal(Views.ActivityId, activity.Id);
        Assert.False(activity.Background);
        var trailing = Assert.IsType<TextWidget>(activity.Trailing);
        Assert.Equal("12 offen · 1 mir", trailing.Text);
        Assert.Equal(BrimColors.Fog, trailing.Color);
        var expanded = Test.Json(activity.Expanded!);
        Assert.Contains("Assigned to Me", expanded);
        Assert.Contains("Passwort zurücksetzen", expanded);
        Assert.Contains("open:1040", expanded);
    }

    [Fact]
    public async Task Overdue_tickets_turn_the_notch_red()
    {
        _source.Queues[1] = [Test.T(1043, "Drucker", overdue: true)];
        _source.Counts[4] = 1;
        var plugin = Create();

        await plugin.TickAsync();

        var trailing = Assert.IsType<TextWidget>(_sink.Last!.Trailing);
        Assert.Equal(BrimColors.Danger, trailing.Color);
        Assert.Contains("1 überfällig", trailing.AccessibleName);
    }

    [Fact]
    public async Task Nothing_open_keeps_the_notch_free_unless_wanted()
    {
        _source.Queues[1] = [];
        var plugin = Create();

        await plugin.TickAsync();
        Assert.True(_sink.Last!.Background);
        Assert.Contains("Keine Tickets", Test.Json(_sink.Last.Expanded!));

        await plugin.ApplySettingsAsync(Configured with { NotchWhenEmpty = true });
        Assert.False(_sink.Last!.Background);
        Assert.Equal("0 offen", ((TextWidget)_sink.Last.Trailing!).Text);
    }

    [Fact]
    public async Task New_ticket_and_new_assignment_are_announced_but_not_what_was_there_at_start()
    {
        _source.Queues[1] = [Test.T(1040, "Passwort zurücksetzen")];
        _source.Queues[6] = [];
        var plugin = Create();
        await plugin.TickAsync();
        Assert.Empty(_sink.Huds);

        _source.Queues[1] = [Test.T(1043, "Drucker druckt nicht", from: "Max Muster"), Test.T(1040, "Passwort zurücksetzen")];
        await NextRefresh(plugin);

        var hud = Assert.Single(_sink.Huds);
        Assert.Equal("Neues Ticket #000231: Drucker druckt nicht · Max Muster", hud.Announcement);

        _source.Queues[6] = [Test.T(1040, "Passwort zurücksetzen", from: "Erika Beispiel")];
        await NextRefresh(plugin);

        Assert.Equal(2, _sink.Huds.Count);
        Assert.Equal("Dir zugewiesen: #000228: Passwort zurücksetzen · Erika Beispiel", _sink.Huds[^1].Announcement);
    }

    [Fact]
    public async Task New_ticket_assigned_right_away_is_announced_once()
    {
        _source.Queues[1] = [Test.T(1040, "Alt")];
        _source.Queues[6] = [];
        var plugin = Create();
        await plugin.TickAsync();

        _source.Queues[1] = [Test.T(1050, "Neu"), Test.T(1040, "Alt")];
        _source.Queues[6] = [Test.T(1050, "Neu")];
        await NextRefresh(plugin);

        var hud = Assert.Single(_sink.Huds);
        Assert.StartsWith("Dir zugewiesen:", hud.Announcement);
    }

    [Fact]
    public async Task Old_ticket_moving_back_to_the_top_is_not_new()
    {
        _source.Queues[1] = [Test.T(1043, "Neuestes")];
        _source.Queues[6] = [];
        var plugin = Create();
        await plugin.TickAsync();

        // Ticket 900 bekam eine Antwort und steht jetzt auf der ersten Seite.
        _source.Queues[1] = [Test.T(900, "Uralt"), Test.T(1043, "Neuestes")];
        await NextRefresh(plugin);

        Assert.Empty(_sink.Huds);
    }

    [Fact]
    public async Task Several_new_tickets_come_as_one_message_and_alerts_can_be_switched_off()
    {
        _source.Queues[1] = [Test.T(1040, "Alt")];
        var plugin = Create(Configured with { MyQueue = "" });
        await plugin.TickAsync();

        _source.Queues[1] = [Test.T(1052, "Drei"), Test.T(1051, "Zwei"), Test.T(1050, "Eins"), Test.T(1040, "Alt")];
        await NextRefresh(plugin);
        Assert.Contains("(und 2 weitere)", Assert.Single(_sink.Huds).Announcement);

        await plugin.ApplySettingsAsync(Configured with { MyQueue = "", Alerts = false });
        _source.Queues[1] = [Test.T(1060, "Vier"), .. _source.Queues[1]];
        await NextRefresh(plugin);
        Assert.Single(_sink.Huds);
    }

    [Fact]
    public async Task Fetches_only_when_the_refresh_is_due()
    {
        _source.Queues[1] = [];
        _source.Queues[6] = [];
        var plugin = Create();

        await plugin.TickAsync();
        _time.Advance(OsTicketPlugin.Tick);
        await plugin.TickAsync();
        Assert.Equal(1, _source.CountCalls);

        _time.Advance(plugin.Settings.Refresh);
        await plugin.TickAsync();
        Assert.Equal(2, _source.CountCalls);
    }

    [Fact]
    public async Task Errors_keep_the_last_tickets_and_say_offline()
    {
        _source.Queues[1] = [Test.T(1043, "Drucker")];
        var plugin = Create();
        await plugin.SetPlacedWidgetsAsync(Placed("tickets", TileSize.Large));
        await plugin.TickAsync();

        _source.Failure = new TicketSourceException("Anmeldung abgelehnt: Invalid login");
        await NextRefresh(plugin);

        var widget = Test.Json(_sink.LastWidget("tickets", TileSize.Large)!);
        Assert.Contains("offline", widget);
        Assert.Contains("Drucker", widget);
        Assert.Contains("Anmeldung abgelehnt: Invalid login", _sink.Logs);
    }

    [Fact]
    public async Task First_error_without_data_shows_the_reason()
    {
        _source.Failure = new HttpRequestException("no route");
        var plugin = Create();
        await plugin.SetPlacedWidgetsAsync(Placed("tickets", TileSize.Medium));

        Assert.Contains("keine Verbindung zu osTicket", Test.Json(_sink.LastWidget("tickets", TileSize.Medium)!));
    }

    [Fact]
    public async Task Not_configured_shows_setup_hint_and_asks_nothing()
    {
        var plugin = Create(new PluginSettings());
        await plugin.SetPlacedWidgetsAsync(Placed("tickets", TileSize.Large));
        await plugin.TickAsync();

        Assert.Equal(0, _source.CountCalls);
        Assert.Empty(_sink.Activities);
        Assert.Contains(Views.SetupText, Test.Json(_sink.LastWidget("tickets", TileSize.Large)!));
    }

    [Fact]
    public async Task Queue_by_name_is_looked_up_in_the_navigation()
    {
        _source.Queues[15] = [Test.T(1043, "Scanner")];
        var plugin = Create(Configured with { Queue = "drucker & scanner", MyQueue = "" });

        await plugin.TickAsync();

        Assert.Equal(new Queue(15, "Drucker & Scanner"), plugin.Main!.Queue);
        Assert.Equal([null, 15], _source.QueueCalls);
        Assert.Equal("1 Ticket", ((TextWidget)_sink.Last!.Trailing!).Text);
    }

    [Fact]
    public async Task Unknown_queue_name_is_an_error()
    {
        var plugin = Create(Configured with { Queue = "Gibt es nicht" });
        await plugin.SetPlacedWidgetsAsync(Placed("tickets", TileSize.Large));

        Assert.Contains("Warteschlange „Gibt es nicht“ gibt es in osTicket nicht", Test.Json(_sink.LastWidget("tickets", TileSize.Large)!));
    }

    [Fact]
    public async Task Clicks_open_tickets_and_queues_in_the_browser()
    {
        var plugin = Create();

        await plugin.HandleActionAsync("open:1043");
        await plugin.HandleActionAsync("queue:6");
        await plugin.HandleActionAsync("queue:0");
        await plugin.HandleActionAsync("open:../x");

        Assert.Equal(["https://support.example.com/scp/tickets.php?id=1043", "https://support.example.com/scp/tickets.php?queue=6"], _opened);
    }

    [Fact]
    public async Task New_login_gets_a_new_source_other_settings_keep_it()
    {
        _source.Queues[1] = [];
        var plugin = Create();
        await plugin.TickAsync();

        await plugin.ApplySettingsAsync(Configured with { Alerts = false });
        Assert.Equal(1, _sourcesCreated);
        Assert.False(_source.Disposed);

        await plugin.ApplySettingsAsync(Configured with { Password = "neu" });
        Assert.Equal(2, _sourcesCreated);
        Assert.True(_source.Disposed);
    }

    [Fact]
    public async Task Widgets_are_sent_for_the_placed_sizes_and_only_when_changed()
    {
        _source.Queues[1] = [Test.T(1043, "Drucker", overdue: true), Test.T(1040, "Passwort", unanswered: false)];
        _source.Queues[6] = [Test.T(1043, "Drucker", overdue: true)];
        _source.Counts[4] = 1;
        var plugin = Create();

        await plugin.SetPlacedWidgetsAsync(new Dictionary<string, IReadOnlyList<TileSize>>
        {
            ["tickets"] = [TileSize.Small, TileSize.Full],
            ["overview"] = [TileSize.Medium],
        });
        var count = _sink.Widgets.Count;
        await NextRefresh(plugin);

        Assert.Equal(3, count);
        Assert.Equal(count, _sink.Widgets.Count);
        var small = Test.Json(_sink.LastWidget("tickets", TileSize.Small)!);
        Assert.Contains("1 überfällig", small);
        Assert.Contains("queue:1", small);
        var full = Test.Json(_sink.LastWidget("tickets", TileSize.Full)!);
        Assert.Contains("Max Muster", full);
        Assert.Contains("open:1043", full);
        var overview = Test.Json(_sink.LastWidget("overview", TileSize.Medium)!);
        Assert.Contains("Assigned to Me", overview);
        Assert.Contains("überfällig", overview);
        Assert.Contains("queue:4", overview);
    }

    [Fact]
    public void Settings_read_text_numbers_and_defaults()
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""
            {"url":" https://support.example.com ","username":"tobias","password":" geheim ","queue":"","myQueue":"","refreshSeconds":"10","alerts":"false"}
            """)!;

        var settings = PluginSettings.From(values);

        Assert.Equal("https://support.example.com", settings.Url);
        Assert.Equal(" geheim ", settings.Password);
        Assert.Equal(PluginSettings.OpenQueue, settings.Queue);
        Assert.Equal("", settings.MyQueue);
        Assert.Equal(TimeSpan.FromSeconds(30), settings.Refresh);
        Assert.False(settings.Alerts);
        Assert.Equal(PluginSettings.AssignedToMeQueue, PluginSettings.From(new Dictionary<string, JsonElement>()).MyQueue);
    }

    [Fact]
    public void Manifest_matches_the_settings_and_widgets()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "OsTicket", "plugin.json")));
        var root = manifest.RootElement;

        Assert.Equal("de.brim.osticket", root.GetProperty("id").GetString());
        Assert.Equal(["tickets", "overview"], root.GetProperty("widgets").EnumerateArray().Select(w => w.GetProperty("id").GetString()));
        var defaults = root.GetProperty("settings").EnumerateArray().ToDictionary(s => s.GetProperty("key").GetString()!, s => s.GetProperty("default"));
        var fromDefaults = PluginSettings.From(defaults);
        Assert.Equal(new PluginSettings(), fromDefaults);
    }
}
