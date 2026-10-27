using System.Globalization;
using System.Text.Json;

namespace Brim.OsTicket;

/// <summary>Einstellungen aus <c>plugin.json</c> mit den Werten des Nutzers. Schalter und Zahlen dürfen auch als Text kommen.</summary>
public sealed record PluginSettings
{
    /// <summary>Warteschlangen, die osTicket bei der Installation anlegt.</summary>
    public const string OpenQueue = "1";
    public const string AssignedToMeQueue = "6";

    /// <summary>Adresse von osTicket, z. B. <c>https://support.example.com</c>.</summary>
    public string Url { get; init; } = "";

    /// <summary>Benutzername oder E-Mail des Agenten.</summary>
    public string Username { get; init; } = "";

    public string Password { get; init; } = "";

    /// <summary>Warteschlange für Notch, Liste und Meldungen: Nummer oder Name, wie in osTicket.</summary>
    public string Queue { get; init; } = OpenQueue;

    /// <summary>Die eigenen Tickets; leer schaltet sie ab.</summary>
    public string MyQueue { get; init; } = AssignedToMeQueue;

    public bool Notch { get; init; } = true;

    /// <summary>Auch in der kompakten Notch, wenn nichts offen ist (sonst nur aufgeklappt).</summary>
    public bool NotchWhenEmpty { get; init; }

    public bool Alerts { get; init; } = true;

    public TimeSpan Refresh { get; init; } = TimeSpan.FromSeconds(60);

    public bool Configured => Url.Length > 0;

    public static PluginSettings From(IReadOnlyDictionary<string, JsonElement> values)
    {
        var defaults = new PluginSettings();
        return new PluginSettings
        {
            Url = Text(values, "url"),
            Username = Text(values, "username"),
            // Passwörter nicht kürzen: Leerzeichen am Rand können dazugehören.
            Password = values.TryGetValue("password", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "",
            Queue = values.ContainsKey("queue") ? Text(values, "queue") is { Length: > 0 } queue ? queue : OpenQueue : defaults.Queue,
            MyQueue = values.ContainsKey("myQueue") ? Text(values, "myQueue") : defaults.MyQueue,
            Notch = Flag(values, "notch", defaults.Notch),
            NotchWhenEmpty = Flag(values, "notchWhenEmpty", defaults.NotchWhenEmpty),
            Alerts = Flag(values, "alerts", defaults.Alerts),
            Refresh = TimeSpan.FromSeconds(Math.Clamp(Number(values, "refreshSeconds", defaults.Refresh.TotalSeconds), 30, 3600)),
        };
    }

    private static string Text(IReadOnlyDictionary<string, JsonElement> values, string key) =>
        values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "").Trim() : "";

    private static double Number(IReadOnlyDictionary<string, JsonElement> values, string key, double fallback)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return fallback;
        }
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString()?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => fallback,
        };
    }

    private static bool Flag(IReadOnlyDictionary<string, JsonElement> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return fallback;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            JsonValueKind.Number => value.GetDouble() != 0,
            _ => fallback,
        };
    }
}
