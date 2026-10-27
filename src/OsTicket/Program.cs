// osTicket-Plugin für Brim: offene und eigene Tickets in der Notch, neue Tickets als kurze Meldung, Ticketliste und
// Übersicht als Widgets auf „Jetzt“. Liest über das Agenten-Panel von osTicket, angemeldet als Agent.
using Brim.OsTicket;
using Brim.Sdk;

BrimPlugin brim;
try
{
    brim = await BrimPlugin.ConnectAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Keine Verbindung zu Brim: {ex.Message}");
    return 1;
}

await using (brim)
{
    var settings = PluginSettings.From(brim.Settings);
    var plugin = new OsTicketPlugin(new BrimSink(brim), Sources.Create, Sources.OpenInBrowser, TimeProvider.System, settings);
    Console.WriteLine(settings.Configured
        ? $"osTicket: {StaffPanelSource.NormalizeUrl(settings.Url)}, Agent: {settings.Username}, Warteschlangen: {settings.Queue} / {settings.MyQueue}"
        : "osTicket ist noch nicht eingerichtet");

    brim.ActionInvoked += (action, value) => _ = plugin.HandleActionAsync(action);
    brim.SettingsChanged += values => _ = plugin.ApplySettingsAsync(PluginSettings.From(values));
    brim.WidgetsPlaced += placed => _ = plugin.SetPlacedWidgetsAsync(placed);
    _ = plugin.SetPlacedWidgetsAsync(brim.PlacedWidgets);

    using var stop = new CancellationTokenSource();
    var loop = plugin.RunAsync(stop.Token);
    await brim.RunAsync();

    // Brim gibt 2 Sekunden zum Beenden.
    stop.Cancel();
    await Task.WhenAny(loop, Task.Delay(500));
    plugin.Source.Dispose();
}
return 0;
