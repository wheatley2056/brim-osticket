using System.Diagnostics;
using System.Net;
using System.Reflection;

namespace Brim.OsTicket;

public static class Sources
{
    /// <summary>
    /// Eine Quelle mit eigenem HttpClient: Jede Anmeldung bringt ihren eigenen Sitzungs-Cookie mit, und eine
    /// neue Adresse oder ein neuer Agent fängt ohne alte Cookies an.
    /// </summary>
    public static ITicketSource Create(PluginSettings settings)
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"BrimOsTicket/{version} (+https://github.com/wheatley2056/brim-osticket)");
        return new StaffPanelSource(http, settings.Url, settings.Username, settings.Password);
    }

    /// <summary>Öffnet eine Adresse im Standardbrowser; nur http(s), damit aus der Seite nichts anderes gestartet wird.</summary>
    public static void OpenInBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Konnte {uri} nicht öffnen: {ex.Message}");
        }
    }
}
