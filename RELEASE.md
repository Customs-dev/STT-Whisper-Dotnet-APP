# Publikace nové verze Prompto

## 1. Zvýšit verzi

V souboru `SttApp.csproj` upravte element `<Version>`:

```xml
<Version>1.1.0</Version>
```

## 2. Commit a push

```powershell
cd c:\Users\u023872\Desktop\STT-Whisper-Dotnet-APP\src\SttApp
git add -A
git commit -m "Release v1.1.0"
git push origin main
```

## 3. Vytvořit a pushnout tag

```powershell
git tag v1.1.0
git push origin v1.1.0
```

> Tag musí odpovídat verzi v `.csproj` s prefixem `v`.

## 4. Sestavit lokálně

Z kořenového adresáře workspace (ne z `src/SttApp`):

```powershell
cd c:\Users\u023872\Desktop\STT-Whisper-Dotnet-APP
.\build-installer.ps1 -SkipInnoSetup
```

Výstup se uloží do `installer\velopack\`.

## 5. Nahrát na GitHub Release

```powershell
cd c:\Users\u023872\Desktop\STT-Whisper-Dotnet-APP
vpk upload github `
    --repoUrl "https://github.com/Customs-dev/STT-Whisper-Dotnet-APP" `
    --tag "v1.1.0" `
    --outputDir "c:\Users\u023872\Desktop\STT-Whisper-Dotnet-APP\installer\velopack" `
    --token $env:VELOPACK_GITHUB_TOKEN
```

> Token je uložen v proměnné prostředí `VELOPACK_GITHUB_TOKEN`.
> Pokud není nastavena, nastavte ji:
> ```powershell
> $env:VELOPACK_GITHUB_TOKEN = "github_pat_..."
> [Environment]::SetEnvironmentVariable("VELOPACK_GITHUB_TOKEN", $env:VELOPACK_GITHUB_TOKEN, "User")
> ```

## Alternativa: GitHub Actions (automaticky)

Pokud je workflow `.github/workflows/release.yml` aktivní, stačí kroky 1–3.
Push tagu `v*` automaticky spustí build + upload na GitHub Release.

## Poznámky

- `--outputDir` musí být **absolutní cesta** (relativní nefunguje spolehlivě)
- Velopack automaticky vytvoří delta balíčky při dalších verzích
- Whisper model (`models/`) se do balíčků nezahrnuje — uživatel ho stahuje zvlášť
- Stávající uživatelé dostanou notifikaci o aktualizaci při startu aplikace
