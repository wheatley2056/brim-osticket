using System.Globalization;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;

namespace Brim.OsTicket.Tests;

internal sealed class RecordingSink : INotchSink
{
    public List<ActivityParams> Activities { get; } = [];
    public List<string> Ended { get; } = [];
    public List<HudParams> Huds { get; } = [];
    public List<(string Id, TileSize Size, Widget Content)> Widgets { get; } = [];
    public List<string> Logs { get; } = [];

    public ActivityParams? Last => Activities.Count > 0 ? Activities[^1] : null;

    public Widget? LastWidget(string id, TileSize size) => Widgets.LastOrDefault(w => w.Id == id && w.Size == size).Content;

    public Task SetActivityAsync(ActivityParams activity)
    {
        Activities.Add(activity);
        return Task.CompletedTask;
    }

    public Task EndActivityAsync(string id)
    {
        Ended.Add(id);
        return Task.CompletedTask;
    }

    public Task ShowHudAsync(HudParams hud)
    {
        Huds.Add(hud);
        return Task.CompletedTask;
    }

    public Task LogAsync(string message, string level)
    {
        Logs.Add(message);
        return Task.CompletedTask;
    }

    public Task SetWidgetAsync(string id, TileSize size, Widget content)
    {
        Widgets.Add((id, size, content));
        return Task.CompletedTask;
    }
}

/// <summary>Quelle mit festen Tickets je Warteschlange; zählt die Abrufe.</summary>
internal sealed class FakeSource : ITicketSource
{
    public Dictionary<int, List<Ticket>> Queues { get; } = [];
    public Dictionary<int, int> Counts { get; } = [];
    public List<Queue> Navigation { get; } =
        [new(1, "Open"), new(4, "Overdue"), new(6, "Assigned to Me"), new(15, "Drucker & Scanner")];
    public Exception? Failure { get; set; }
    public List<int?> QueueCalls { get; } = [];
    public int CountCalls { get; private set; }
    public bool Disposed { get; private set; }

    public Task<IReadOnlyDictionary<int, int>> CountsAsync(CancellationToken cancellationToken)
    {
        CountCalls++;
        if (Failure is { } failure)
        {
            throw failure;
        }
        var counts = new Dictionary<int, int>(Counts);
        foreach (var (id, tickets) in Queues)
        {
            counts.TryAdd(id, tickets.Count);
        }
        return Task.FromResult<IReadOnlyDictionary<int, int>>(counts);
    }

    public Task<QueuePage> QueueAsync(int? queueId, CancellationToken cancellationToken)
    {
        QueueCalls.Add(queueId);
        if (Failure is { } failure)
        {
            throw failure;
        }
        var tickets = queueId is { } id ? Queues.GetValueOrDefault(id) ?? [] : [];
        return Task.FromResult(new QueuePage(tickets.ToList(), Navigation));
    }

    public string TicketUrl(int ticketId) => $"https://support.example.com/scp/tickets.php?id={ticketId}";

    public string QueueUrl(int queueId) => $"https://support.example.com/scp/tickets.php?queue={queueId}";

    public void Dispose() => Disposed = true;
}

/// <summary>Antwortet je Pfadanfang und Methode mit Status und Inhalt; merkt sich die Anfragen samt Formularen.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    public List<(HttpMethod Method, string Url, string? Body, string? RequestedWith)> Requests { get; } = [];
    public List<(HttpMethod Method, string Path, Func<HttpResponseMessage> Respond)> Routes { get; } = [];

    public void On(HttpMethod method, string path, HttpStatusCode status, string body) =>
        Routes.Insert(0, (method, path, () => new HttpResponseMessage(status) { Content = new StringContent(body) }));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var url = request.RequestUri!.ToString();
        Requests.Add((request.Method, url, body, request.Headers.TryGetValues("X-Requested-With", out var v) ? v.First() : null));
        foreach (var (method, path, respond) in Routes)
        {
            if (method == request.Method && url.Contains(path, StringComparison.Ordinal))
            {
                return respond();
            }
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

internal static class Test
{
    /// <summary>Dienstag, 27.10.2026, 8:30.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 27, 8, 30, 0, TimeSpan.FromHours(1));

    public static void Setup() => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

    public static string Data(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", name));

    public static Ticket T(int id, string subject, string from = "Max Muster", bool overdue = false, bool unanswered = true) => new()
    {
        Id = id,
        Number = (id - 812).ToString("000000", CultureInfo.InvariantCulture),
        Subject = subject,
        From = from,
        Overdue = overdue,
        Unanswered = unanswered,
    };

    private static readonly JsonSerializerOptions Readable = new(PluginJson.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Lesbar statt · usw., damit die Tests nach Text suchen können.</summary>
    public static string Json(object value) => JsonSerializer.Serialize(value, value.GetType(), Readable);
}
