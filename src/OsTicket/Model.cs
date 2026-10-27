namespace Brim.OsTicket;

/// <summary>Eine Warteschlange aus der Navigation von osTicket, z. B. 1 „Open“ oder 6 „Assigned to Me“.</summary>
public sealed record Queue(int Id, string Name);

/// <summary>Ein Ticket, wie es in der Liste einer Warteschlange steht. Was die Liste nicht zeigt, bleibt leer.</summary>
public sealed record Ticket
{
    /// <summary>Interne Kennung (<c>tickets.php?id=…</c>), gleich über mehrere Abrufe.</summary>
    public required int Id { get; init; }

    /// <summary>Ticketnummer wie „000123“ oder „TK-4711“.</summary>
    public required string Number { get; init; }

    public string Subject { get; init; } = "";

    /// <summary>Wer das Ticket geschrieben hat.</summary>
    public string From { get; init; } = "";

    public string Priority { get; init; } = "";

    public string Assignee { get; init; } = "";

    public string Department { get; init; } = "";

    /// <summary>Zuletzt geändert oder erstellt, so wie osTicket es formatiert („27.10.26 08:14“).</summary>
    public string Updated { get; init; } = "";

    public bool Overdue { get; init; }

    /// <summary>Noch ohne Antwort eines Agenten (osTicket zeigt solche Tickets fett).</summary>
    public bool Unanswered { get; init; }
}

/// <summary>Eine Seite einer Warteschlange: die ersten Tickets und die Warteschlangen aus der Navigation.</summary>
public sealed record QueuePage(IReadOnlyList<Ticket> Tickets, IReadOnlyList<Queue> Queues);

/// <summary>Was eine Warteschlange gerade zeigt: Anzahl, erste Tickets und wann sie geholt wurden, oder warum nicht.</summary>
public sealed record QueueBoard(Queue Queue, int? Count, IReadOnlyList<Ticket> Tickets, DateTimeOffset? UpdatedAt, string? Error);

/// <summary>Woher die Tickets kommen.</summary>
public interface ITicketSource : IDisposable
{
    /// <summary>Anzahl Tickets je Warteschlange (Kennung → Anzahl), wie in der Navigation von osTicket.</summary>
    Task<IReadOnlyDictionary<int, int>> CountsAsync(CancellationToken cancellationToken);

    /// <summary>Erste Seite einer Warteschlange; <c>null</c> ist die Startseite der Tickets.</summary>
    Task<QueuePage> QueueAsync(int? queueId, CancellationToken cancellationToken);

    /// <summary>Adresse eines Tickets im Browser.</summary>
    string TicketUrl(int ticketId);

    /// <summary>Adresse einer Warteschlange im Browser.</summary>
    string QueueUrl(int queueId);
}

/// <summary>osTicket hat geantwortet, aber nicht wie erwartet (Anmeldung abgelehnt, Fehlercode). Die Meldung ist für Nutzer.</summary>
public sealed class TicketSourceException(string message, Exception? inner = null) : Exception(message, inner);
