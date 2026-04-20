namespace SttApp;

/// <summary>
/// Statický seznam změn pro každou verzi aplikace.
/// Při vydání nové verze stačí přidat nový záznam na začátek slovníku.
/// </summary>
public static class Changelog
{
    public static readonly IReadOnlyList<ChangelogEntry> Entries = new List<ChangelogEntry>
    {
        new("1.3.2", "Push-to-Talk režim", new[]
        {
            "Nový režim nahrávání Push-to-Talk – drž klávesovou zkratku pro nahrávání, pusť pro přepis",
            "Volba režimu (Toggle / Push-to-Talk) v Nastavení",
            "Balloon notifikace zobrazuje aktuální režim a ovládání",
        }),
        new("1.3.1", "Oprava aktualizace (v3)", new[]
        {
            "Update nejprve vyprázdní current\\ a poté spustí Update.exe apply (prázdný adresář jde přejmenovat)",
            "Fallback: pokud Update.exe selže, soubory se zkopírují ručně z nupkg",
            "Skript pre-reinstall.bat pro přípravu ruční reinstalace přes Setup.exe",
        }),
        new("1.3.0", "Spolehlivá aktualizace", new[]
        {
            "Zcela přepracovaný mechanismus aktualizace – obchází problém Windows se zámkem na adresáři",
            "Update nejprve vyprázdní current\\ a poté spustí Update.exe apply (prázdný adresář jde přejmenovat)",
            "Fallback: pokud Update.exe selže, soubory se zkopírují ručně z nupkg",
            "Detailní logovací soubor update-helper.log pro diagnostiku",
            "Skript pre-reinstall.bat pro přípravu ruční reinstalace přes Setup.exe",
        }),
        new("1.2.9", "Oprava aktualizací (v2)", new[]
        {
            "Vyřešen přetrvávající problém PermissionDenied při updatu",
            "Update.exe se nyní spouští až 20 sekund po ukončení aplikace (dostatek času pro uvolnění file-handlů)",
            "Zabráněno souběžnému spuštění více instancí Update.exe",
        }),
        new("1.2.8", "Novinky po aktualizaci", new[]
        {
            "Nová obrazovka \"Co je nového\" – zobrazí se automaticky po aktualizaci na novou verzi",
            "Položka \"Co je nového\" v tray menu pro ruční zobrazení",
            "Changelog s přehledem změn pro každou verzi",
        }),
        new("1.2.7", "Oprava aktualizací", new[]
        {
            "Opravena chyba PermissionDenied při automatické aktualizaci",
            "Nativní knihovny (Whisper, NAudio) se nyní korektně uvolní před aplikací updatu",
            "Odstraněn PowerShell pomocný skript – update nyní probíhá přes standardní Velopack mechanismus",
            "Přidáno zobrazení novinek po aktualizaci (tato obrazovka)",
        }),
        new("1.2.6", "Historie přepisů & nastavení schránky", new[]
        {
            "Nová funkce: Historie přepisů – zobrazí posledních 50 přepisů s možností kopírování",
            "Nové nastavení: Kopírování přepisu do schránky lze vypnout",
            "Vylepšené ikony tlačítek (Segoe MDL2 Assets místo emoji)",
            "Opraveno rozložení okna Nastavení",
        }),
        new("1.2.5", "Live streaming & vylepšení", new[]
        {
            "Live streaming přepisu po chunkách (nastavitelný interval)",
            "WebSocket server pro integraci s externími aplikacemi",
            "Podpora Push-to-Talk režimu nahrávání",
        }),
    };
}

public sealed record ChangelogEntry(string Version, string Title, string[] Changes);
