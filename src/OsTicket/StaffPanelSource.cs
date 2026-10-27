using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Brim.OsTicket;

/// <summary>
/// Liest Tickets über das Agenten-Panel von osTicket (<c>/scp</c>), angemeldet als Agent. Die offizielle API von
/// osTicket kann nur Tickets anlegen, nicht lesen; das Panel zeigt dagegen genau, was der Agent selbst sieht.
/// Die Zahlen kommen von <c>ajax.php/queue/counts</c> (JSON, wie die Zähler in der Navigation), die Tickets aus der
/// Liste <c>tickets.php?queue=…</c>. Läuft die Sitzung ab, meldet sich die Quelle einmal neu an.
/// </summary>
public sealed partial class StaffPanelSource(HttpClient http, string baseUrl, string username, string password) : ITicketSource
{
    private readonly SemaphoreSlim _login = new(1, 1);
    private bool _loggedIn;

    /// <summary>Adresse ohne <c>/scp</c> und ohne Schrägstrich am Ende, z. B. <c>https://support.example.com</c>.</summary>
    public string BaseUrl { get; } = NormalizeUrl(baseUrl);

    private string Scp => BaseUrl + "/scp/";

    public string TicketUrl(int ticketId) => $"{Scp}tickets.php?id={ticketId}";

    public string QueueUrl(int queueId) => $"{Scp}tickets.php?queue={queueId}";

    public async Task<IReadOnlyDictionary<int, int>> CountsAsync(CancellationToken cancellationToken)
    {
        var body = await GetAsync("ajax.php/queue/counts", ajax: true, cancellationToken);
        try
        {
            using var json = JsonDocument.Parse(body);
            return ParseCounts(json.RootElement);
        }
        catch (JsonException ex)
        {
            throw new TicketSourceException("osTicket hat unlesbare Zahlen geschickt", ex);
        }
    }

    public async Task<QueuePage> QueueAsync(int? queueId, CancellationToken cancellationToken)
    {
        var html = await GetAsync(queueId is { } id ? $"tickets.php?queue={id}" : "tickets.php", ajax: false, cancellationToken);
        return new QueuePage(ParseTickets(html), ParseQueues(html));
    }

    public void Dispose()
    {
        http.Dispose();
        _login.Dispose();
    }

    // ───────── Anfragen und Anmeldung ─────────

    /// <summary>Holt eine Seite des Panels; ist die Sitzung abgelaufen, meldet es sich an und versucht es noch einmal.</summary>
    private async Task<string> GetAsync(string path, bool ajax, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (!_loggedIn)
            {
                await LoginAsync(cancellationToken);
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, Scp + path);
            if (ajax)
            {
                request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            }
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            // Ohne Sitzung antwortet ajax.php mit 403, die übrigen Seiten zeigen das Anmeldeformular.
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized || IsLoginPage(body))
            {
                _loggedIn = false;
                if (attempt == 0)
                {
                    continue;
                }
                throw new TicketSourceException("osTicket lässt den Agenten nicht hinein; Rechte prüfen");
            }
            ThrowOnError(response.StatusCode);
            return body;
        }
    }

    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        await _login.WaitAsync(cancellationToken);
        try
        {
            if (_loggedIn)
            {
                return;
            }
            if (username.Length == 0 || password.Length == 0)
            {
                throw new TicketSourceException("Benutzername und Passwort unter Einstellungen → Plugins → osTicket eintragen");
            }

            // Das Formular bringt das CSRF-Token und den Sitzungs-Cookie mit.
            using var page = await http.GetAsync(Scp + "login.php", cancellationToken);
            ThrowOnError(page.StatusCode);
            var html = await page.Content.ReadAsStringAsync(cancellationToken);
            var token = ParseCsrfToken(html)
                ?? throw new TicketSourceException("Unter dieser Adresse antwortet kein osTicket (Anmeldeseite nicht gefunden)");

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__CSRFToken__"] = token,
                ["do"] = "scplogin",
                ["userid"] = username,
                ["passwd"] = password,
                ["ajax"] = "1",
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, Scp + "login.php") { Content = form };
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            using var response = await http.SendAsync(request, cancellationToken);
            ThrowOnError(response.StatusCode);
            CheckLogin(await response.Content.ReadAsStringAsync(cancellationToken));
            _loggedIn = true;
        }
        finally
        {
            _login.Release();
        }
    }

    private static void ThrowOnError(HttpStatusCode status)
    {
        if ((int)status is >= 200 and < 300)
        {
            return;
        }
        throw new TicketSourceException(status switch
        {
            HttpStatusCode.NotFound => "Unter dieser Adresse gibt es kein osTicket (404)",
            HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout =>
                $"osTicket antwortet gerade nicht ({(int)status})",
            _ => $"osTicket meldet Fehler {(int)status}",
        });
    }

    // ───────── Antworten lesen (öffentlich für Tests) ─────────

    /// <summary>
    /// Antwort auf die Anmeldung. osTicket ab 1.17 antwortet mit JSON: <c>{"status":302,"redirect":…}</c> heißt
    /// angemeldet, außer es geht zurück auf <c>login.php</c> (zweiter Faktor fehlt); <c>{"status":401,"message":…}</c>
    /// heißt abgelehnt. Ältere Versionen leiten auf eine HTML-Seite weiter.
    /// </summary>
    public static void CheckLogin(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            using var json = JsonDocument.Parse(trimmed);
            var root = json.RootElement;
            var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0;
            var redirect = root.TryGetProperty("redirect", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
            if (status == 302 && !redirect.Contains("login.php", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (status == 302)
            {
                throw new TicketSourceException(TwoFactorMessage);
            }
            var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            throw new TicketSourceException(string.IsNullOrWhiteSpace(message)
                ? "Anmeldung abgelehnt: Benutzername oder Passwort stimmt nicht"
                : "Anmeldung abgelehnt: " + Text(message));
        }
        if (body.Contains("value=\"2fa\"", StringComparison.Ordinal))
        {
            throw new TicketSourceException(TwoFactorMessage);
        }
        if (IsLoginPage(body))
        {
            throw new TicketSourceException("Anmeldung abgelehnt: Benutzername oder Passwort stimmt nicht");
        }
    }

    public const string TwoFactorMessage = "Der Agent braucht einen zweiten Faktor; das Plugin kann sich nur mit Benutzername und Passwort anmelden";

    /// <summary>Das Anmeldeformular des Panels erkennt man am Passwortfeld.</summary>
    public static bool IsLoginPage(string html) =>
        html.Contains("name=\"passwd\"", StringComparison.Ordinal) && html.Contains("scplogin", StringComparison.Ordinal);

    public static string? ParseCsrfToken(string html) =>
        CsrfInput().Match(html) is { Success: true } input ? WebUtility.HtmlDecode(input.Groups[1].Value)
        : CsrfMeta().Match(html) is { Success: true } meta ? WebUtility.HtmlDecode(meta.Groups[1].Value)
        : null;

    /// <summary><c>{"q1":"12","q6":"1.234","q9":"-"}</c> → {1: 12, 6: 1234}; „-“ (nicht zählbar) fehlt.</summary>
    public static IReadOnlyDictionary<int, int> ParseCounts(JsonElement root)
    {
        var counts = new Dictionary<int, int>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            return counts;
        }
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.StartsWith('q') || !int.TryParse(property.Name.AsSpan(1), out var id))
            {
                continue;
            }
            var count = property.Value.ValueKind switch
            {
                JsonValueKind.Number when property.Value.TryGetInt32(out var n) => n,
                // Format::number schreibt Tausenderpunkte je nach Sprache („1.234“, „1,234“, „1 234“).
                JsonValueKind.String when property.Value.GetString() is { } text && text.Any(char.IsAsciiDigit)
                    && int.TryParse(string.Concat(text.Where(char.IsAsciiDigit)), out var n) => n,
                _ => (int?)null,
            };
            if (count is { } c)
            {
                counts[id] = c;
            }
        }
        return counts;
    }

    /// <summary>Warteschlangen aus der Navigation: <c>&lt;a … href="tickets.php?queue=6" title="Assigned to Me"&gt;</c>.</summary>
    public static IReadOnlyList<Queue> ParseQueues(string html)
    {
        var queues = new List<Queue>();
        foreach (Match match in QueueLink().Matches(html))
        {
            var id = int.Parse(match.Groups["id"].Value);
            var name = Text(match.Groups["title"].Success ? match.Groups["title"].Value : match.Groups["text"].Value);
            if (name.Length > 0 && queues.All(q => q.Id != id))
            {
                queues.Add(new Queue(id, name));
            }
        }
        return queues;
    }

    /// <summary>
    /// Die Ticketliste (<c>table.queue.tickets</c>). Welche Spalte was ist, sagt <c>data-id</c> im Kopf: Die
    /// mitgelieferten Spalten von osTicket haben feste Kennungen (1 Nummer, 3 Betreff, 4 Absender …). Selbst
    /// angelegte Spalten erkennt die Quelle an der Überschrift. Fett heißt unbeantwortet, das Symbol
    /// <c>overdueTicket</c> überfällig.
    /// </summary>
    public static IReadOnlyList<Ticket> ParseTickets(string html)
    {
        var tickets = new List<Ticket>();
        var table = TicketTable().Match(html);
        if (!table.Success)
        {
            return tickets;
        }
        var columns = HeaderCell().Matches(table.Groups["head"].Value)
            .Select(m => Column(int.Parse(m.Groups["id"].Value), Text(m.Groups["text"].Value)))
            .ToList();
        foreach (Match row in Row().Matches(table.Groups["body"].Value))
        {
            var rowHtml = row.Groups[1].Value;
            if (TicketLink().Match(rowHtml) is not { Success: true } link)
            {
                continue;
            }
            // Wer Tickets verwalten darf, hat vorne eine Spalte mit Ankreuzfeld ohne Kopf in data-id.
            var cells = Cell().Matches(rowHtml).Where(c => !c.Value.Contains("class=\"ckb\"", StringComparison.Ordinal)).ToList();
            string Get(ColumnRole role) => columns.IndexOf(role) is var i and >= 0 && i < cells.Count ? CellText(cells[i].Groups["content"].Value) : "";

            var id = int.Parse(link.Groups[1].Value);
            var number = PreviewLink().Match(rowHtml) is { Success: true } preview ? Text(preview.Groups[1].Value) : Get(ColumnRole.Number);
            var subject = SubjectLink(rowHtml, id) ?? Get(ColumnRole.Subject);
            tickets.Add(new Ticket
            {
                Id = id,
                Number = number.Length > 0 ? number : id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Subject = subject,
                From = Get(ColumnRole.From),
                Priority = Get(ColumnRole.Priority),
                Assignee = Get(ColumnRole.Assignee),
                Department = Get(ColumnRole.Department),
                Updated = Get(ColumnRole.Updated) is { Length: > 0 } updated ? updated : Get(ColumnRole.Created),
                Overdue = rowHtml.Contains("overdueTicket", StringComparison.Ordinal),
                Unanswered = cells.Any(c => BoldStyle().IsMatch(c.Groups["attrs"].Value)),
            });
        }
        return tickets;
    }

    public enum ColumnRole { Other, Number, Created, Subject, From, Priority, Status, Closed, Assignee, Due, Updated, Department }

    /// <summary>Kennungen der Spalten aus der Grundausstattung von osTicket (<c>queue_column.yaml</c>), sonst die Überschrift.</summary>
    public static ColumnRole Column(int id, string heading) => id switch
    {
        1 => ColumnRole.Number,
        2 => ColumnRole.Created,
        3 => ColumnRole.Subject,
        4 => ColumnRole.From,
        5 => ColumnRole.Priority,
        6 => ColumnRole.Status,
        7 => ColumnRole.Closed,
        8 => ColumnRole.Assignee,
        9 => ColumnRole.Due,
        10 => ColumnRole.Updated,
        11 => ColumnRole.Department,
        _ => heading.ToLowerInvariant() switch
        {
            "ticket" or "ticket #" or "nummer" or "ticketnummer" => ColumnRole.Number,
            "subject" or "betreff" => ColumnRole.Subject,
            "from" or "user name" or "von" or "benutzer" or "benutzername" => ColumnRole.From,
            "priority" or "priorität" => ColumnRole.Priority,
            "assignee" or "assigned to" or "zugewiesen" or "zugewiesen an" or "bearbeiter" => ColumnRole.Assignee,
            "last updated" or "zuletzt aktualisiert" or "letzte aktualisierung" => ColumnRole.Updated,
            "department" or "abteilung" => ColumnRole.Department,
            _ => ColumnRole.Other,
        },
    };

    /// <summary>Der Betreff ist der Link aufs Ticket ohne Vorschau (die Nummer hat <c>class="preview"</c>).</summary>
    private static string? SubjectLink(string rowHtml, int id)
    {
        foreach (Match link in AnyTicketLink().Matches(rowHtml))
        {
            if (link.Groups["id"].Value == id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                && !link.Groups["attrs"].Value.Contains("preview", StringComparison.Ordinal))
            {
                return Text(link.Groups["text"].Value);
            }
        }
        return null;
    }

    /// <summary>Zelleninhalt ohne Beiwerk: Zähler für Antworten und Anhänge (<c>&lt;small&gt;</c>) und Symbole fallen weg.</summary>
    private static string CellText(string html) => Text(Small().Replace(html, " "));

    /// <summary>HTML → Text: Tags weg, Entities auflösen, Leerraum zusammenziehen.</summary>
    public static string Text(string? html) =>
        Whitespace().Replace(WebUtility.HtmlDecode(Tag().Replace(html ?? "", " ")), " ").Trim();

    /// <summary>„support.example.com/scp/“ → „https://support.example.com“; „http://intranet/helpdesk/scp/login.php“ → „http://intranet/helpdesk“.</summary>
    public static string NormalizeUrl(string? url)
    {
        var text = (url ?? "").Trim();
        if (text.Length == 0)
        {
            return "";
        }
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }
        var query = text.IndexOfAny(['?', '#']);
        if (query >= 0)
        {
            text = text[..query];
        }
        text = text.TrimEnd('/');
        foreach (var page in new[] { "/login.php", "/index.php", "/tickets.php" })
        {
            if (text.EndsWith(page, StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^page.Length];
            }
        }
        if (text.EndsWith("/scp", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^"/scp".Length];
        }
        return text.TrimEnd('/');
    }

    [GeneratedRegex("""name=["']__CSRFToken__["']\s+value=["']([^"']+)["']""")]
    private static partial Regex CsrfInput();

    [GeneratedRegex("""<meta\s+name=["']csrf_token["']\s+content=["']([^"']+)["']""")]
    private static partial Regex CsrfMeta();

    [GeneratedRegex("""<a\b[^>]*?href="tickets\.php\?queue=(?<id>\d+)"(?:\s+title="(?<title>[^"]*)"|[^>]*>(?:\s*<i[^>]*></i>)?(?<text>[^<]+))""", RegexOptions.IgnoreCase)]
    private static partial Regex QueueLink();

    [GeneratedRegex("""<table\b[^>]*class="[^"]*\bqueue\b[^"]*\btickets\b[^"]*"[^>]*>.*?<thead>(?<head>.*?)</thead>.*?<tbody>(?<body>.*?)</tbody>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TicketTable();

    [GeneratedRegex("""<th\b[^>]*\bdata-id="(?<id>\d+)"[^>]*>(?<text>.*?)</th>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex HeaderCell();

    [GeneratedRegex("""<tr\b[^>]*>(.*?)</tr>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Row();

    [GeneratedRegex("""<td\b(?<attrs>[^>]*)>(?<content>.*?)</td>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Cell();

    [GeneratedRegex("""tickets\.php\?id=(\d+)""")]
    private static partial Regex TicketLink();

    [GeneratedRegex("""<a\b[^>]*class="preview"[^>]*>(.*?)</a>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex PreviewLink();

    [GeneratedRegex("""<a\b(?<attrs>[^>]*)href="[^"]*tickets\.php\?id=(?<id>\d+)"[^>]*>(?<text>.*?)</a>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex AnyTicketLink();

    [GeneratedRegex("""font-weight:\s*bold""", RegexOptions.IgnoreCase)]
    private static partial Regex BoldStyle();

    [GeneratedRegex("""<small\b[^>]*>.*?</small>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Small();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
