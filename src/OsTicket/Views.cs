using System.Globalization;
using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;

namespace Brim.OsTicket;

/// <summary>Alle Inhalte des Plugins als Widget-Bäume. Reine Funktionen, damit Tests sie prüfen können.</summary>
public static class Views
{
    /// <summary>Segoe Fluent Icons „Headset“: der Helpdesk.</summary>
    public static readonly string TicketGlyph = "";

    public const string Accent = "#5AA9E6";

    public const string ActivityId = "tickets";
    public const string RefreshAction = "refresh";

    /// <summary>„open:123“ öffnet Ticket 123 im Browser.</summary>
    public const string OpenTicketPrefix = "open:";

    /// <summary>„queue:6“ öffnet Warteschlange 6 im Browser.</summary>
    public const string OpenQueuePrefix = "queue:";

    public const string SetupText = "osTicket unter Einstellungen → Plugins → osTicket einrichten";

    /// <summary>Die Zahlen auf einen Blick: Haupt-Warteschlange, eigene und überfällige Tickets.</summary>
    public sealed record Summary(QueueBoard? Main, QueueBoard? Mine, int? Overdue);

    // ───────── Texte ─────────

    /// <summary>„12 offen · 3 mir“; bei einer anderen Warteschlange „12 Tickets“.</summary>
    public static string Compact(Summary s, CultureInfo culture)
    {
        var main = s.Main?.Count ?? 0;
        var text = main.ToString(culture) + (s.Main?.Queue.Id == 1 ? " offen" : main == 1 ? " Ticket" : " Tickets");
        if (s.Mine is { Count: > 0 and var mine } && s.Mine.Queue.Id != s.Main?.Queue.Id)
        {
            text += $" · {mine.ToString(culture)} mir";
        }
        return text;
    }

    /// <summary>Für die Sprachausgabe: „12 Tickets in Open, 3 in Assigned to Me, 1 überfällig“.</summary>
    public static string Describe(Summary s, CultureInfo culture)
    {
        var parts = new List<string>();
        if (s.Main is { } main)
        {
            parts.Add($"{(main.Count ?? 0).ToString(culture)} Tickets in {main.Queue.Name}");
        }
        if (s.Mine is { } mine && mine.Queue.Id != s.Main?.Queue.Id)
        {
            parts.Add($"{(mine.Count ?? 0).ToString(culture)} in {mine.Queue.Name}");
        }
        if (s.Overdue is > 0 and var overdue)
        {
            parts.Add($"{overdue.ToString(culture)} überfällig");
        }
        return string.Join(", ", parts);
    }

    /// <summary>„#000123 Drucker druckt nicht, von Max Muster“.</summary>
    public static string Describe(Ticket t)
    {
        var text = $"#{t.Number} {t.Subject}";
        if (t.From.Length > 0)
        {
            text += ", von " + t.From;
        }
        if (t.Overdue)
        {
            text += ", überfällig";
        }
        return text;
    }

    // ───────── Notch ─────────

    /// <summary>
    /// Die Aktivität „tickets“: kompakt Symbol und Anzahl, rot, sobald etwas überfällig ist. Ist nichts offen,
    /// bleibt sie im Hintergrund (nur aufgeklappt zu sehen), außer der Nutzer will sie immer sehen.
    /// </summary>
    public static ActivityParams Activity(Summary s, bool background, Widget expanded, CultureInfo culture)
    {
        var offline = s.Main?.Error is not null;
        var color = s.Overdue > 0 ? BrimColors.Danger : offline ? BrimColors.Slate : BrimColors.Fog;
        return new ActivityParams
        {
            Id = ActivityId,
            Priority = ActivityPriority.Ongoing,
            Leading = new IconWidget(TicketGlyph) { Size = 14, Color = s.Overdue > 0 ? BrimColors.Danger : Accent },
            Trailing = new TextWidget(Compact(s, culture))
            {
                Style = TextStyle.Caption,
                FontSize = 12,
                Color = color,
                AccessibleName = Describe(s, culture),
            },
            Bead = new IconWidget(TicketGlyph) { Size = 8, Color = Accent },
            Expanded = expanded,
            Accent = Accent,
            Rank = 3,
            Background = background,
        };
    }

    /// <summary>Aufgeklappt: eigene Tickets, darunter die Haupt-Warteschlange.</summary>
    public static Widget Expanded(Summary s)
    {
        var children = new List<Widget>();
        var showMine = s.Mine is { Tickets.Count: > 0 } && s.Mine.Queue.Id != s.Main?.Queue.Id;
        if (showMine)
        {
            children.Add(BoardHeader(s.Mine!, null));
            children.AddRange(s.Mine!.Tickets.Take(3).Select(t => TicketRow(t, true)));
        }
        if (s.Main is { } main)
        {
            if (children.Count > 0)
            {
                children.Add(new SpacerWidget { Size = 6 });
            }
            children.Add(BoardHeader(main, s.Overdue));
            children.AddRange(main.Tickets.Take(showMine ? 4 : 7).Select(t => TicketRow(t, true)));
            if (main.Tickets.Count == 0)
            {
                children.Add(Note(EmptyText(main)));
            }
        }
        return new StackWidget(children)
        {
            Orientation = StackOrientation.Vertical,
            Spacing = 4,
            Align = CrossAlign.Stretch,
        };
    }

    /// <summary>Kurze Meldung: „Neues Ticket #000123: Drucker druckt nicht · Max Muster“.</summary>
    public static HudParams AlertHud(string title, Ticket ticket, int more)
    {
        var text = $"{title} #{ticket.Number}: {ticket.Subject}";
        if (ticket.From.Length > 0)
        {
            text += " · " + ticket.From;
        }
        if (more > 0)
        {
            text += more == 1 ? " (und 1 weiteres)" : $" (und {more} weitere)";
        }
        return new HudParams
        {
            Id = "alert",
            Content = new StackWidget(
                new IconWidget(TicketGlyph) { Size = 16, Color = ticket.Overdue ? BrimColors.Danger : Accent },
                new TextWidget(text) { Style = TextStyle.Body, Grow = 1 })
            {
                Spacing = 12,
                Padding = (18, 0, 18, 0),
            },
            DurationMs = 8000,
            Announcement = text,
        };
    }

    // ───────── Widget „Tickets“ ─────────

    public static Widget TicketsWidget(QueueBoard? board, bool configured, int? overdue, TileSize size, CultureInfo culture)
    {
        if (!configured)
        {
            return TileCard.Empty(TicketGlyph, size == TileSize.Small ? "Nicht eingerichtet" : SetupText);
        }
        if (board is null)
        {
            return TileCard.Empty(TicketGlyph, "Lädt …");
        }
        if (size == TileSize.Small)
        {
            return Card(BoardHeader(board, null), SmallBody(board, overdue, culture), OpenQueuePrefix + board.Queue.Id);
        }
        var header = BoardHeader(board, overdue);
        if (board.Tickets.Count == 0)
        {
            return Card(header, Note(EmptyText(board)), OpenQueuePrefix + board.Queue.Id);
        }
        var wide = size is TileSize.Wide or TileSize.Full;
        var rows = size is TileSize.Large or TileSize.Full ? 6 : 2;
        return Card(header, new StackWidget(board.Tickets.Take(rows).Select(t => TicketRow(t, wide)).ToList())
        {
            Orientation = StackOrientation.Vertical,
            Spacing = 2,
            Align = CrossAlign.Stretch,
        }, null);
    }

    /// <summary>Kleine Kachel: groß die Anzahl, darunter wie viele überfällig sind.</summary>
    private static Widget SmallBody(QueueBoard board, int? overdue, CultureInfo culture) => new StackWidget(
        new TextWidget(board.Count is { } count ? count.ToString(culture) : "–") { Style = TextStyle.Title },
        new TextWidget(board.Error is not null ? "offline" : overdue > 0 ? $"{overdue.Value.ToString(culture)} überfällig" : "offen")
        {
            Style = TextStyle.Caption,
            Color = board.Error is not null ? BrimColors.Warning : overdue > 0 ? BrimColors.Danger : null,
        })
    {
        Spacing = 6,
        Align = CrossAlign.End,
    };

    /// <summary>
    /// Eine Zeile: Nummer, Betreff (hell, solange unbeantwortet), breit auch Absender und Zeit. Überfällige Tickets
    /// haben eine rote Nummer. Ein Klick öffnet das Ticket im Browser.
    /// </summary>
    public static Widget TicketRow(Ticket t, bool wide)
    {
        var children = new List<Widget>
        {
            new TextWidget(t.Number) { Style = TextStyle.Mono, FontSize = 12, Color = t.Overdue ? BrimColors.Danger : BrimColors.Slate, FixedWidth = wide ? 64 : 54 },
            new TextWidget(t.Subject.Length > 0 ? t.Subject : "(ohne Betreff)")
            {
                Style = t.Unanswered ? TextStyle.Body : TextStyle.Secondary,
                FontSize = 12,
                Grow = 1,
            },
        };
        if (wide && t.From.Length > 0)
        {
            children.Add(new TextWidget(t.From) { Style = TextStyle.Caption, FixedWidth = 110 });
        }
        if (wide && t.Updated.Length > 0)
        {
            children.Add(new TextWidget(t.Updated) { Style = TextStyle.Caption, FixedWidth = 96 });
        }
        return new StackWidget(children)
        {
            Spacing = 6,
            ActionId = OpenTicketPrefix + t.Id.ToString(CultureInfo.InvariantCulture),
            AccessibleName = Describe(t),
        };
    }

    // ───────── Widget „Übersicht“ ─────────

    /// <summary>Drei Zahlen nebeneinander: Haupt-Warteschlange, eigene, überfällige. Ein Klick öffnet die Warteschlange.</summary>
    public static Widget OverviewWidget(Summary s, bool configured, int overdueQueue, TileSize size, CultureInfo culture)
    {
        if (!configured)
        {
            return TileCard.Empty(TicketGlyph, size == TileSize.Small ? "Nicht eingerichtet" : SetupText);
        }
        if (s.Main is null)
        {
            return TileCard.Empty(TicketGlyph, "Lädt …");
        }
        var header = Header("osTicket", s.Main.Error is null ? null : "offline", BrimColors.Warning, OpenQueuePrefix + s.Main.Queue.Id);
        var figures = new List<Widget> { Figure(s.Main.Count, s.Main.Queue.Name, null, OpenQueuePrefix + s.Main.Queue.Id, culture) };
        if (s.Mine is { } mine && mine.Queue.Id != s.Main.Queue.Id)
        {
            figures.Add(Figure(mine.Count, size == TileSize.Small ? "mir" : mine.Queue.Name, null, OpenQueuePrefix + mine.Queue.Id, culture));
        }
        if (size != TileSize.Small && s.Overdue is { } overdue)
        {
            figures.Add(Figure(overdue, "überfällig", overdue > 0 ? BrimColors.Danger : null, OpenQueuePrefix + overdueQueue, culture));
        }
        return TileCard.Card(header, new StackWidget(figures) { Spacing = 12, Justify = size == TileSize.Small ? Justify.SpaceBetween : Justify.Start });
    }

    private static Widget Figure(int? value, string label, string? color, string actionId, CultureInfo culture) => new StackWidget(
        new TextWidget(value is { } v ? v.ToString(culture) : "–") { Style = TextStyle.Title, Color = color },
        new TextWidget(label) { Style = TextStyle.Caption })
    {
        Spacing = 5,
        Align = CrossAlign.End,
        ActionId = actionId,
        AccessibleName = $"{(value is { } n ? n.ToString(culture) : "keine Angabe")} {label}",
    };

    // ───────── Bausteine ─────────

    /// <summary>Kopfzeile einer Warteschlange: Name und Anzahl, bei Fehlern „offline“. Ein Klick öffnet sie im Browser.</summary>
    public static StackWidget BoardHeader(QueueBoard board, int? overdue)
    {
        string? trailing = board.Error is not null ? "offline"
            : overdue > 0 ? $"{board.Count} · {overdue} überfällig"
            : board.Count?.ToString(CultureInfo.CurrentCulture);
        var color = board.Error is not null ? BrimColors.Warning : overdue > 0 ? BrimColors.Danger : null;
        return Header(board.Queue.Name, trailing, color, OpenQueuePrefix + board.Queue.Id);
    }

    /// <summary>Kopfzeile wie <see cref="TileCard.Header"/>, rechts optional ein farbiger Hinweis.</summary>
    public static StackWidget Header(string title, string? trailing, string? trailingColor, string? actionId)
    {
        var children = new List<Widget>
        {
            new IconWidget(TicketGlyph) { Size = 12, Color = Accent },
            new TextWidget(title) { Style = TextStyle.Caption, Grow = 1 },
        };
        if (trailing is not null)
        {
            children.Add(new TextWidget(trailing) { Style = TextStyle.Caption, Color = trailingColor });
        }
        return new StackWidget(children) { Spacing = 6, ActionId = actionId };
    }

    private static Widget Card(StackWidget header, Widget body, string? actionId) =>
        TileCard.Card(header, body) with { ActionId = actionId };

    private static Widget Note(string text) => new TextWidget(text) { Style = TextStyle.Secondary, FontSize = 12, Wrap = true };

    private static string EmptyText(QueueBoard board) =>
        board.Error is not null ? board.Error : board.UpdatedAt is null ? "Lädt …" : "Keine Tickets, alles erledigt";
}
