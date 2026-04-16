# Prompto

Prompto je Windows tray aplikace (WinForms, .NET 9) pro speech-to-text přepis pomocí Whisper.

## Hlavní funkce

- nahrávání přes globální hotkey
- přepis hlasu do textu přes Whisper (Vulkan + CPU fallback)
- vložení textu na aktuální kurzor (Notepad, Chrome, Outlook, další Win32 editory)
- volitelný WebSocket server pro stream segmentů
- volitelný chunked režim přepisu (live stream)

## Požadavky

- Windows 10/11 x64
- .NET 9 SDK (pro vývoj)
- Whisper model soubor `.bin` (není součástí repozitáře ani installeru)

Doporučený model:

- `ggml-large-v3.bin`
- URL: `https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin`

Umístění modelu v runtime:

- `%APPDATA%\Prompto\models\ggml-large-v3.bin`

## Spuštění v dev režimu

```powershell
dotnet run --project "src\SttApp\SttApp.csproj"
```

## Build

```powershell
dotnet build "src\SttApp\SttApp.csproj" -c Release
```

## Installer

Projekt obsahuje Inno Setup script a build skript:

- `installer/Prompto.iss`
- `build-installer.ps1`

Vytvoření instalátoru:

```powershell
.\build-installer.ps1
```

Výstup:

- `installer\Output\PromptoSetup.exe`

## Nastavení

Nastavení uživatele se ukládá do:

- `%APPDATA%\Prompto\settings.json`

Důležitá pole:

- `EnableWebSocket` - zapnutí WS serveru
- `WebSocketPort` - port WS serveru (default 5050)
- `ChunkIntervalSeconds` - interval chunkingu; aktivní jen při zapnutém WebSocketu

## WebSocket stream

Při zapnutém WebSocketu server publikuje průběžné segmenty na:

- `ws://localhost:5050/stt/` (nebo port dle nastavení)

Poznámka: některé weby blokují `localhost` přes CSP. Pro test použijte lokální HTML stránku (např. `ws-test.html`).

## Struktura projektu

- `Program.cs` - vstupní bod
- `TrayApp.cs` - hotkey pipeline, chunking, paste logika, WS integrace
- `WhisperTranscriber.cs` - Whisper inicializace a přepis
- `AudioRecorder.cs` - nahrávání zvuku
- `TextInjector.cs` - vkládání textu do cílových aplikací
- `WebSocketServer.cs` - lokální WS server
- `SettingsForm.cs` - UI nastavení
- `AboutForm.cs` - dialog O aplikaci

## Troubleshooting

### Native Library not found - Whisper Library

Použijte aktuální installer vytvořený přes `build-installer.ps1`, který balí multi-file publish včetně `runtimes\...\whisper.dll`.

### Bitmap image is not valid (installer)

Zkontrolujte, že Inno Setup script nepoužívá ICO jako `WizardSmallImageFile`.

### WebSocket se nepřipojí ve webu

Pokud browser hlásí CSP `connect-src` blokaci, není to chyba Prompto serveru. Připojte se z lokální stránky nebo z aplikace bez CSP omezení.
