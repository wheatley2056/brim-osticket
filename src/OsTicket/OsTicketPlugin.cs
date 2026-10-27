using System.Globalization;
using System.Text.Json;
using Brim.Core;
using Brim.Core.Plugins;

namespace Brim.OsTicket;

/// <summary>
/// Holt regelmäßig die Zahlen und die ersten Tickets der eingestellten Warteschlangen, zeigt die Anzahl in der Notch
/// und die Listen als Widgets. Kommt ein neues Ticket herein oder wird dir eins zugewiesen, meldet es sich kurz.
/// Schickt nur, was sich geändert hat.
/// </summary>
public sealed class OsTicketPlugin
{
    /// <summary>So oft wird nachgesehen, ob neue Daten fällig sind.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    /// <summary>Warteschlange „Overdue“, die osTicket bei der Installation anlegt.</summary>
    public const int OverdueQueue = 4;

    /// <summary>So viele Ticket-Kennungen merkt sich das Plugin je Warteschlange, um Neues zu erkennen.</summary>
    public const int RememberedTickets = 2000;

    private readonly INotchSink _sink;
    private readonly Func<PluginSettings, ITicketSource> _createSource;
    private readonly Action<string> _open;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _sent = [];
    private readonly Dictionary<int, HashSet<int>> _seen = [];
    private IReadOnlyDictionary<string, IReadOnlyList<TileSize>> _placed = new Dictionary<string, IReadOnlyList<TileSize>>();
    private IReadOnlyList<Queue> _queues = [];
    private DateTimeOffset? _fetchedAt;
    private bool _activityShown;

    /// <summary>Höchste Ticket-Kennung, die schon zu sehen war; neue Tickets haben eine größere.</summary>
    private int? _newestTicket;

    public OsTicketPlugin(INotchSink sink, Func<PluginSettings, ITicketSource> createSource, Action<string> open, TimeProvider time, PluginSettings settings)
    {
        _sink = sink;
        _createSource = createSource;
        _open = open;
        _time = time;
        Settings = settings;
        Source = createSource(settings);
    }

    public PluginSettings Settings { get; private set; }

    public ITicketSource Source { get; private set; }

    public QueueBoard? Main { get; private set; }

    public QueueBoard? Mine { get; private set; }

    public int? Overdue { get; private set; }

    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    private bool Placed(string widget) => _placed.GetValueOrDefault(widget) is { Count: > 0 };

    private Views.Summary Summary => new(Main, Mine, Overdue);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            try
            {
                await Task.Delay(Tick, _time, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow();
            if (_fetchedAt is not { } fetched || now - fetched >= Settings.Refresh)
            {
                await FetchAsync(now, cancellationToken);
                await RenderAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ───────── Daten ─────────

    private async Task FetchAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        _fetchedAt = now;
        if (!Settings.Configured)
        {
            return;
        }
        // Nur holen, was jemand sieht oder was Meldungen brauchen.
        var wanted = Settings.Notch || Settings.Alerts || Placed("tickets") || Placed("overview");
        if (!wanted)
        {
            return;
        }
        try
        {
            var counts = await Source.CountsAsync(cancellationToken);
            Overdue = counts.TryGetValue(OverdueQueue, out var overdue) ? overdue : null;

            var main = await ResolveAsync(Settings.Queue, cancellationToken);
            var mainPage = await Source.QueueAsync(main.Id, cancellationToken);
            Remember(mainPage.Queues);
            main = Named(main);
            Main = new QueueBoard(main, counts.TryGetValue(main.Id, out var c) ? c : mainPage.Tickets.Count, mainPage.Tickets, now, null);

            QueuePage? minePage = null;
            if (Settings.MyQueue.Length > 0 && (Settings.Alerts || Settings.Notch || Placed("overview")))
            {
                var mine = await ResolveAsync(Settings.MyQueue, cancellationToken);
                minePage = mine.Id == main.Id ? mainPage : await Source.QueueAsync(mine.Id, cancellationToken);
                mine = Named(mine);
                Mine = new QueueBoard(mine, counts.TryGetValue(mine.Id, out var m) ? m : minePage.Tickets.Count, minePage.Tickets, now, null);
            }
            else
            {
                Mine = null;
            }
            await AlertAsync(mainPage, minePage);
        }
        catch (Exception ex) when (IsSourceError(ex, cancellationToken))
        {
            await _sink.LogAsync(ex.Message, "warn");
            var error = Message(ex);
            Main = Main is { } old ? old with { Error = error }
                : new QueueBoard(new Queue(0, "osTicket"), null, [], null, error);
            Mine = Mine is { } oldMine ? oldMine with { Error = error } : null;
        }
    }

    /// <summary>Nummer oder Name aus den Einstellungen → Warteschlange. Für Namen holt es einmal die Navigation.</summary>
    private async Task<Queue> ResolveAsync(string text, CancellationToken cancellationToken)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return Named(new Queue(id, DefaultName(id)));
        }
        if (Find(text) is { } known)
        {
            return known;
        }
        Remember((await Source.QueueAsync(null, cancellationToken)).Queues);
        return Find(text) ?? throw new TicketSourceException($"Warteschlange „{text}“ gibt es in osTicket nicht");
    }

    private Queue? Find(string name) => _queues.FirstOrDefault(q => string.Equals(q.Name, name, StringComparison.CurrentCultureIgnoreCase));

    private Queue Named(Queue queue) => _queues.FirstOrDefault(q => q.Id == queue.Id) ?? queue;

    private void Remember(IReadOnlyList<Queue> queues)
    {
        if (queues.Count > 0)
        {
            _queues = queues;
        }
    }

    /// <summary>Namen der Warteschlangen, die osTicket mitbringt, falls die Navigation fehlt.</summary>
    private static string DefaultName(int id) => id switch
    {
        1 => "Offen",
        4 => "Überfällig",
        5 => "Meine Tickets",
        6 => "Mir zugewiesen",
        7 => "Meinen Teams zugewiesen",
        8 => "Geschlossen",
        _ => $"Warteschlange {id}",
    };

    private static bool IsSourceError(Exception ex, CancellationToken cancellationToken) =>
        ex is TicketSourceException or HttpRequestException or JsonException
        || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static string Message(Exception ex) => ex switch
    {
        TicketSourceException => ex.Message,
        OperationCanceledException => "osTicket antwortet nicht",
        HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException } => "Zertifikat von osTicket ungültig",
        HttpRequestException => "keine Verbindung zu osTicket",
        _ => "unlesbare Daten",
    };

    /// <summary>
    /// Meldet neue Tickets in der Haupt-Warteschlange und Tickets, die neu in deiner stehen. Was beim ersten Abruf schon
    /// da ist, gilt als bekannt. „Neu“ in der Haupt-Warteschlange heißt: größere Kennung als alles bisher Gesehene, damit
    /// ein altes Ticket, das nach einer Antwort wieder nach oben rutscht, nicht als neu gilt.
    /// </summary>
    private async Task AlertAsync(QueuePage main, QueuePage? mine)
    {
        var newest = _newestTicket;
        var mineNew = mine is null || Mine is null ? [] : Unseen(Mine.Queue.Id, mine.Tickets);
        var mainNew = Unseen(Main!.Queue.Id, main.Tickets).Where(t => newest is { } n && t.Id > n).ToList();
        _newestTicket = main.Tickets.Concat(mine?.Tickets ?? []).Select(t => (int?)t.Id).Append(newest).Max();
        if (!Settings.Alerts)
        {
            return;
        }
        if (mineNew.Count > 0)
        {
            await _sink.ShowHudAsync(Views.AlertHud("Dir zugewiesen:", mineNew[0], mineNew.Count - 1));
        }
        var others = mainNew.Where(t => mineNew.All(m => m.Id != t.Id)).ToList();
        if (others.Count > 0)
        {
            await _sink.ShowHudAsync(Views.AlertHud("Neues Ticket", others[0], others.Count - 1));
        }
    }

    /// <summary>Tickets, die in dieser Warteschlange noch nicht zu sehen waren; beim ersten Mal keine.</summary>
    private List<Ticket> Unseen(int queueId, IReadOnlyList<Ticket> tickets)
    {
        if (!_seen.TryGetValue(queueId, out var seen))
        {
            _seen[queueId] = tickets.Select(t => t.Id).ToHashSet();
            return [];
        }
        var unseen = tickets.Where(t => !seen.Contains(t.Id)).ToList();
        if (seen.Count + unseen.Count > RememberedTickets)
        {
            seen.Clear();
            seen.UnionWith(tickets.Select(t => t.Id));
        }
        seen.UnionWith(unseen.Select(t => t.Id));
        return unseen;
    }

    // ───────── Anzeigen ─────────

    private async Task RenderAsync()
    {
        var summary = Summary;
        if (Settings.Configured && Settings.Notch && Main is not null)
        {
            var empty = (Main.Count ?? 0) == 0 && (Mine?.Count ?? 0) == 0;
            var activity = Views.Activity(summary, empty && !Settings.NotchWhenEmpty, Views.Expanded(summary), Culture);
            if (Changed("activity", activity))
            {
                await _sink.SetActivityAsync(activity);
                _activityShown = true;
            }
        }
        else if (_activityShown)
        {
            await _sink.EndActivityAsync(Views.ActivityId);
            _sent.Remove("activity");
            _activityShown = false;
        }

        foreach (var size in _placed.GetValueOrDefault("tickets") ?? [])
        {
            var widget = Views.TicketsWidget(Main, Settings.Configured, Overdue, size, Culture);
            if (Changed("tickets:" + size, widget))
            {
                await _sink.SetWidgetAsync("tickets", size, widget);
            }
        }
        foreach (var size in _placed.GetValueOrDefault("overview") ?? [])
        {
            var widget = Views.OverviewWidget(summary, Settings.Configured, OverdueQueue, size, Culture);
            if (Changed("overview:" + size, widget))
            {
                await _sink.SetWidgetAsync("overview", size, widget);
            }
        }
    }

    /// <summary>Merkt sich, was zuletzt geschickt wurde; <c>true</c>, wenn <paramref name="value"/> neu ist.</summary>
    private bool Changed(string key, object value)
    {
        var json = JsonSerializer.Serialize(value, value.GetType(), PluginJson.Options);
        if (_sent.TryGetValue(key, out var sent) && sent == json)
        {
            return false;
        }
        _sent[key] = json;
        return true;
    }

    // ───────── Ereignisse von Brim ─────────

    public async Task SetPlacedWidgetsAsync(IReadOnlyDictionary<string, IReadOnlyList<TileSize>> placed)
    {
        await _gate.WaitAsync();
        try
        {
            var wanted = Main is null && (placed.GetValueOrDefault("tickets") is { Count: > 0 } || placed.GetValueOrDefault("overview") is { Count: > 0 });
            _placed = placed;
            // Neu platziert und noch nichts da: sofort holen statt bis zum nächsten Abruf „Lädt …“ zu zeigen.
            if (wanted)
            {
                await FetchAsync(_time.GetUtcNow(), CancellationToken.None);
            }
            await RenderAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplySettingsAsync(PluginSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            if (settings.Url != Settings.Url || settings.Username != Settings.Username || settings.Password != Settings.Password)
            {
                Source.Dispose();
                Source = _createSource(settings);
                _queues = [];
                _seen.Clear();
                _newestTicket = null;
                Main = null;
                Mine = null;
                Overdue = null;
            }
            if (settings.Queue != Settings.Queue)
            {
                Main = null;
            }
            if (settings.MyQueue != Settings.MyQueue)
            {
                Mine = null;
            }
            Settings = settings;
            var now = _time.GetUtcNow();
            await FetchAsync(now, CancellationToken.None);
            await RenderAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task HandleActionAsync(string actionId)
    {
        if (actionId.StartsWith(Views.OpenTicketPrefix, StringComparison.Ordinal)
            && int.TryParse(actionId.AsSpan(Views.OpenTicketPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var ticket))
        {
            _open(Source.TicketUrl(ticket));
            return;
        }
        if (actionId.StartsWith(Views.OpenQueuePrefix, StringComparison.Ordinal)
            && int.TryParse(actionId.AsSpan(Views.OpenQueuePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var queue)
            && queue > 0)
        {
            _open(Source.QueueUrl(queue));
            return;
        }
        if (actionId == Views.RefreshAction)
        {
            await _gate.WaitAsync();
            try
            {
                await FetchAsync(_time.GetUtcNow(), CancellationToken.None);
                await RenderAsync();
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
