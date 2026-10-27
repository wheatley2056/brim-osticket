using System.Net;
using System.Text.Json;

namespace Brim.OsTicket.Tests;

public sealed class StaffPanelTests
{
    public StaffPanelTests() => Test.Setup();

    [Fact]
    public void Tickets_come_from_the_queue_table_with_number_subject_sender_and_flags()
    {
        var tickets = StaffPanelSource.ParseTickets(Test.Data("queue.html"));

        Assert.Equal([1043, 1040], tickets.Select(t => t.Id));
        var first = tickets[0];
        Assert.Equal("000231", first.Number);
        // Zähler für Antworten (3) und Anhänge fallen weg, Entities werden aufgelöst.
        Assert.Equal("Drucker im 2. OG druckt nicht & blinkt", first.Subject);
        Assert.Equal("Max Muster", first.From);
        Assert.Equal("High", first.Priority);
        Assert.Equal("Tobias Kern", first.Assignee);
        Assert.Equal("27.10.26 08:14", first.Updated);
        Assert.True(first.Overdue);
        Assert.True(first.Unanswered);
        var second = tickets[1];
        Assert.Equal("Passwort zurücksetzen", second.Subject);
        Assert.Equal("", second.Assignee);
        Assert.False(second.Overdue);
        Assert.False(second.Unanswered);
    }

    [Fact]
    public void Tickets_also_parse_without_the_checkbox_column_of_managers()
    {
        var html = Test.Data("queue.html")
            .Replace("<th style=\"width:12px\"></th>", "")
            .Replace("<td><input type=\"checkbox\" class=\"ckb\" name=\"tids[]\"\n            value=\"1043\" /></td>", "")
            .Replace("<td><input type=\"checkbox\" class=\"ckb\" name=\"tids[]\"\n            value=\"1040\" /></td>", "");

        var tickets = StaffPanelSource.ParseTickets(html);

        Assert.Equal("Max Muster", tickets[0].From);
        Assert.Equal("Erika Beispiel", tickets[1].From);
    }

    [Fact]
    public void Empty_queue_and_foreign_pages_give_no_tickets()
    {
        Assert.Empty(StaffPanelSource.ParseTickets(Test.Data("login.html")));
        var empty = Test.Data("queue.html");
        var start = empty.IndexOf("<tbody>", StringComparison.Ordinal) + "<tbody>".Length;
        var end = empty.IndexOf("</tbody>", StringComparison.Ordinal);
        Assert.Empty(StaffPanelSource.ParseTickets(empty[..start] + empty[end..]));
    }

    [Fact]
    public void Queues_come_from_the_navigation_including_top_level_and_personal_ones()
    {
        var queues = StaffPanelSource.ParseQueues(Test.Data("queue.html"));

        Assert.Contains(new Queue(1, "Open"), queues);
        Assert.Contains(new Queue(3, "Answered"), queues);
        Assert.Contains(new Queue(5, "My Tickets"), queues);
        Assert.Contains(new Queue(6, "Assigned to Me"), queues);
        Assert.Contains(new Queue(15, "Drucker & Scanner"), queues);
        Assert.DoesNotContain(queues, q => q.Name == "Search");
    }

    [Fact]
    public void Counts_read_localized_numbers_and_skip_uncountable_queues()
    {
        using var json = JsonDocument.Parse("""{"q1":"1.234","q4":"0","q6":3,"q9":"-","x":"5"}""");

        var counts = StaffPanelSource.ParseCounts(json.RootElement);

        Assert.Equal(1234, counts[1]);
        Assert.Equal(0, counts[4]);
        Assert.Equal(3, counts[6]);
        Assert.False(counts.ContainsKey(9));
        Assert.Equal(3, counts.Count);
    }

    [Theory]
    [InlineData("support.example.com", "https://support.example.com")]
    [InlineData("https://support.example.com/", "https://support.example.com")]
    [InlineData("https://support.example.com/scp/", "https://support.example.com")]
    [InlineData("http://intranet/helpdesk/scp/login.php", "http://intranet/helpdesk")]
    [InlineData("https://support.example.com/scp/tickets.php?queue=1", "https://support.example.com")]
    [InlineData("  ", "")]
    public void Address_is_normalized_to_the_installation(string input, string expected) =>
        Assert.Equal(expected, StaffPanelSource.NormalizeUrl(input));

    [Fact]
    public void Csrf_token_comes_from_the_login_form_or_the_meta_tag()
    {
        Assert.Equal("7f3a9c1e2b4d&x", StaffPanelSource.ParseCsrfToken(Test.Data("login.html")));
        Assert.Equal("abc", StaffPanelSource.ParseCsrfToken("""<meta name="csrf_token" content="abc" />"""));
        Assert.Null(StaffPanelSource.ParseCsrfToken("<html></html>"));
    }

    [Fact]
    public void Login_answers_are_understood()
    {
        StaffPanelSource.CheckLogin("""{"status":302,"redirect":"index.php"}""");
        // Ältere Versionen leiten auf die Startseite weiter.
        StaffPanelSource.CheckLogin(Test.Data("queue.html"));

        var wrong = Assert.Throws<TicketSourceException>(() => StaffPanelSource.CheckLogin("""{"status":401,"message":"Access denied","show_reset":true}"""));
        Assert.Equal("Anmeldung abgelehnt: Access denied", wrong.Message);
        var twoFactor = Assert.Throws<TicketSourceException>(() => StaffPanelSource.CheckLogin("""{"status":302,"redirect":"login.php"}"""));
        Assert.Equal(StaffPanelSource.TwoFactorMessage, twoFactor.Message);
        Assert.Throws<TicketSourceException>(() => StaffPanelSource.CheckLogin(Test.Data("login.html")));
    }

    private static (StaffPanelSource Source, FakeHttp Http) Panel(string user = "tobias", string password = "geheim")
    {
        var http = new FakeHttp();
        http.On(HttpMethod.Get, "/scp/login.php", HttpStatusCode.OK, Test.Data("login.html"));
        http.On(HttpMethod.Post, "/scp/login.php", HttpStatusCode.OK, """{"status":302,"redirect":"index.php"}""");
        http.On(HttpMethod.Get, "/scp/ajax.php/queue/counts", HttpStatusCode.OK, """{"q1":"2","q4":"1","q6":"1"}""");
        http.On(HttpMethod.Get, "/scp/tickets.php?queue=1", HttpStatusCode.OK, Test.Data("queue.html"));
        return (new StaffPanelSource(new HttpClient(http), "https://support.example.com/support/scp/", user, password), http);
    }

    [Fact]
    public async Task Logs_in_once_with_the_form_token_and_then_reads_counts_and_queues()
    {
        var (source, http) = Panel();

        var counts = await source.CountsAsync(CancellationToken.None);
        var page = await source.QueueAsync(1, CancellationToken.None);

        Assert.Equal(2, counts[1]);
        Assert.Equal(2, page.Tickets.Count);
        Assert.Contains(new Queue(6, "Assigned to Me"), page.Queues);
        var post = Assert.Single(http.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal("https://support.example.com/support/scp/login.php", post.Url);
        Assert.Contains("__CSRFToken__=7f3a9c1e2b4d%26x", post.Body);
        Assert.Contains("do=scplogin", post.Body);
        Assert.Contains("userid=tobias", post.Body);
        Assert.Contains("passwd=geheim", post.Body);
        Assert.Contains(http.Requests, r => r.Url.EndsWith("/ajax.php/queue/counts", StringComparison.Ordinal) && r.RequestedWith == "XMLHttpRequest");
        Assert.Equal("https://support.example.com/support/scp/tickets.php?id=1043", source.TicketUrl(1043));
    }

    [Fact]
    public async Task Expired_session_logs_in_again_once()
    {
        var (source, http) = Panel();
        await source.CountsAsync(CancellationToken.None);
        var expired = true;
        http.Routes.Insert(0, (HttpMethod.Get, "/scp/ajax.php/queue/counts", () => expired
            ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("Must login: Session timed out") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"q1":"5"}""") }));
        http.Routes.Insert(0, (HttpMethod.Post, "/scp/login.php", () =>
        {
            expired = false;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"status":302,"redirect":"index.php"}""") };
        }));

        var counts = await source.CountsAsync(CancellationToken.None);

        Assert.Equal(5, counts[1]);
        Assert.Equal(2, http.Requests.Count(r => r.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task Ticket_page_showing_the_login_form_counts_as_expired()
    {
        var (source, http) = Panel();
        await source.CountsAsync(CancellationToken.None);
        http.On(HttpMethod.Get, "/scp/tickets.php?queue=1", HttpStatusCode.OK, Test.Data("login.html"));

        var error = await Assert.ThrowsAsync<TicketSourceException>(() => source.QueueAsync(1, CancellationToken.None));

        Assert.Contains("lässt den Agenten nicht hinein", error.Message);
        Assert.Equal(2, http.Requests.Count(r => r.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task Wrong_password_and_missing_osticket_give_readable_errors()
    {
        var (source, http) = Panel();
        http.On(HttpMethod.Post, "/scp/login.php", HttpStatusCode.OK, """{"status":401,"message":"Invalid login"}""");
        var wrong = await Assert.ThrowsAsync<TicketSourceException>(() => source.CountsAsync(CancellationToken.None));
        Assert.Equal("Anmeldung abgelehnt: Invalid login", wrong.Message);

        var (other, otherHttp) = Panel();
        otherHttp.On(HttpMethod.Get, "/scp/login.php", HttpStatusCode.OK, "<html><body>Willkommen</body></html>");
        var missing = await Assert.ThrowsAsync<TicketSourceException>(() => other.CountsAsync(CancellationToken.None));
        Assert.Contains("kein osTicket", missing.Message);

        var (empty, _) = Panel(user: "", password: "");
        var setup = await Assert.ThrowsAsync<TicketSourceException>(() => empty.CountsAsync(CancellationToken.None));
        Assert.Contains("Benutzername und Passwort", setup.Message);
    }
}
