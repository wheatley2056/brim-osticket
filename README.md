# osTicket-Plugin für Brim

Zeigt deine Tickets aus [osTicket](https://osticket.com) in der Notch und auf „Jetzt“.

- **In der Notch:** kompakt die Zahl der offenen Tickets und wie viele dir zugewiesen sind, z. B. „12 offen · 3 mir“. Rot heißt: Mindestens ein Ticket ist überfällig. Aufgeklappt siehst du deine Tickets und die neuesten aus der Warteschlange. Ist nichts offen, bleibt die Notch frei.
- **Meldungen:** Kommt ein neues Ticket herein oder wird dir eins zugewiesen, meldet sich die Notch kurz, z. B. „Neues Ticket #000231: Drucker druckt nicht · Max Muster“.
- **Widget „Tickets“** (klein, mittel, breit, groß, ganz): die ersten Tickets einer Warteschlange mit Nummer und Betreff, breit auch Absender und letzte Änderung. Unbeantwortete Tickets stehen hell, überfällige haben eine rote Nummer. Ein Klick öffnet das Ticket im Browser.
- **Widget „Ticket-Übersicht“** (klein, mittel, breit): offen, mir zugewiesen und überfällig auf einen Blick. Ein Klick auf eine Zahl öffnet die Warteschlange.

## Wie das Plugin an die Tickets kommt

Die API von osTicket kann nur Tickets **anlegen**, nicht lesen. Das Plugin meldet sich deshalb wie du im Browser als Agent im Agenten-Panel (`/scp`) an und liest dort die Zähler der Warteschlangen und die Ticketlisten. Es sieht also genau das, was du als Agent siehst, und ändert nichts an den Tickets. Auf dem osTicket-Server muss nichts installiert werden.

Getestet ist das Plugin gegen den Aufbau von osTicket 1.17 und 1.18. Ältere Versionen ab 1.11 sollten gehen, sind aber nicht geprüft.

**Einschränkungen**

- **Zwei-Faktor-Anmeldung:** Hat dein Agent 2FA, kann sich das Plugin nicht anmelden. Dann einen eigenen Agenten ohne 2FA mit Leserechten anlegen oder 2FA für deinen Agenten abschalten.
- **Anmeldung nur über SSO** (z. B. nur Microsoft/OAuth) geht nicht; Benutzername und Passwort, auch aus LDAP oder Active Directory, gehen.
- Das Passwort steht in Brims `settings.json` in deinem Benutzerprofil, wie die Schlüssel anderer Plugins. In den Einstellungen zeigt Brim es verdeckt (ab der Version nach 2.6.0; ältere zeigen es als normales Textfeld).

## Rechte

Brim fragt beim Einschalten nach dem Recht **„Netzwerk“**. Das Plugin spricht nur mit deinem osTicket.

## Installieren

**Aus der Bibliothek:** Brim → Einstellungen → Plugins → Bibliothek → osTicket → Installieren.

**Fertig gebaut:** `brim-osticket.zip` aus dem neuesten Release nach `%LocalAppData%\Brim\Plugins\osticket` entpacken, Brim neu starten und im Tab **Plugins** „osTicket“ einschalten.

**Selbst bauen:** Das Plugin nutzt `Brim.Sdk` aus dem Brim-Repository. Beide Repositories nebeneinander klonen:

```powershell
git clone https://github.com/wheatley2056/brim.git
git clone https://github.com/wheatley2056/brim-osticket.git
cd brim-osticket
dotnet publish src/OsTicket -c Release -r win-x64 --self-contained false -o "$env:LOCALAPPDATA\Brim\Plugins\osticket"
```

Liegt Brim woanders: `-p:BrimRoot=C:\Pfad\zu\brim`.

## Einstellungen

Brim → Einstellungen → Plugins → osTicket:

- **Adresse von osTicket:** wie im Browser, z. B. `https://support.firma.de` oder `https://intranet/helpdesk`. `/scp` am Ende darf dabei sein.
- **Benutzername oder E-Mail** und **Passwort** des Agenten.
- **Warteschlange für Notch, Liste und Meldungen** (`1`): Nummer oder Name, wie in osTicket. Die Nummer steht in der Adresse, wenn du die Warteschlange öffnest (`tickets.php?queue=1`). osTicket legt bei der Installation u. a. an: 1 Offen, 4 Überfällig, 5 Meine Tickets, 6 Mir zugewiesen, 8 Geschlossen.
- **Deine Tickets** (`6`, „Mir zugewiesen“): steht als zweite Zahl in der Notch und meldet neu zugewiesene Tickets. Leer schaltet es ab.
- **Anzahl der Tickets in der Notch zeigen** (an), **auch wenn nichts offen ist** (aus)
- **Melden, wenn ein neues Ticket kommt oder dir eins zugewiesen wird** (an)
- **Alle wie viele Sekunden nachsehen** (60, ab 30)

Als „neu“ gilt ein Ticket, das neuer ist als alle, die das Plugin seit dem Start gesehen hat. Ein altes Ticket, das nach einer Antwort wieder oben steht, meldet es nicht. Die Zahl „überfällig“ kommt aus der Warteschlange 4 („Overdue“); hast du sie gelöscht, fehlt sie.

## Wenn etwas nicht klappt

Das Protokoll liegt in `%LocalAppData%\Brim\logs\plugins\de.brim.osticket.log`. Dort steht beim Start, mit welcher Adresse und welchem Agenten das Plugin arbeitet, und jeder Fehler beim Abruf.

- **„Unter dieser Adresse antwortet kein osTicket“:** die Adresse prüfen. Sie ist die, unter der im Browser `…/scp/login.php` die Anmeldung zeigt.
- **„Anmeldung abgelehnt: …“:** Benutzername oder Passwort stimmt nicht, oder der Agent ist gesperrt. Dahinter steht die Meldung von osTicket.
- **„Der Agent braucht einen zweiten Faktor“:** siehe Einschränkungen.
- **„Warteschlange „…“ gibt es in osTicket nicht“:** den Namen genau wie in der Navigation von osTicket schreiben oder die Nummer eintragen.
- **„Zertifikat von osTicket ungültig“:** Windows vertraut dem Zertifikat des Servers nicht. Das Zertifikat der Firmen-CA in Windows importieren.
