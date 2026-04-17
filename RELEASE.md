# Publikace nové verze Prompto

---

## Varianta A: Automaticky přes GitHub Actions (doporučeno)

Stačí 3 kroky — workflow udělá build, pack i upload za vás.

### 1. Zvýšit verzi

V souboru `SttApp.csproj` upravte element `<Version>`:

```xml
<Version>1.2.0</Version>
```

### 2. Commit & push

```powershell
cd c:\Users\u023872\Desktop\STT-Whisper-Dotnet-APP\src\SttApp
git add -A
git commit -m "Release v1.2.0"
git push origin Implemntation-VeloPack-distribution-model
```

### 3. Vytvořit a pushnout tag

```powershell
git tag v1.2.0
git push origin v1.2.0
```

> Tag musí odpovídat verzi v `.csproj` s prefixem `v`.
> Push tagu `v*` automaticky spustí workflow `.github/workflows/release.yml`,
> který sestaví aplikaci, zabalí přes Velopack a nahraje na GitHub Release.

**Hotovo — nic dalšího dělat nemusíte.**

---

## Varianta B: Manuální release (bez CI/CD)

Použijte pouze pokud GitHub Actions nefungují nebo chcete release udělat lokálně.

### 1–3. Stejné jako varianta A

(Bump verze, commit, push, tag.)

### 4. Sestavit lokálně

Z kořenového adresáře workspace (ne z `src/SttApp`):

```powershell
cd c:\Users\u023872\Desktop\STT-Whisper-Dotnet-APP
.\build-installer.ps1 -SkipInnoSetup
```

Výstup se uloží do `installer\velopack\`.

### 5. Nahrát na GitHub Release

```powershell
cd c:\Users\u023872\Desktop\STT-Whisper-Dotnet-APP
vpk upload github `
    --repoUrl "https://github.com/Customs-dev/STT-Whisper-Dotnet-APP" `
    --tag "v1.2.0" `
    --outputDir "c:\Users\u023872\Desktop\STT-Whisper-Dotnet-APP\installer\velopack" `
    --token $env:VELOPACK_GITHUB_TOKEN `
    --publish
```

> Token je uložen v proměnné prostředí `VELOPACK_GITHUB_TOKEN`.
> Pokud není nastavena, nastavte ji:
> ```powershell
> $env:VELOPACK_GITHUB_TOKEN = "github_pat_..."
> [Environment]::SetEnvironmentVariable("VELOPACK_GITHUB_TOKEN", $env:VELOPACK_GITHUB_TOKEN, "User")
> ```

> **Důležité:** Pokud použijete manuální upload, **nepushujte tag** — jinak
> se spustí i workflow a nahrávání selže kvůli duplicitním souborům.

---

## Poznámky

- `--outputDir` musí být **absolutní cesta** (relativní nefunguje spolehlivě)
- Velopack automaticky vytvoří delta balíčky při dalších verzích
- Whisper model (`models/`) se do balíčků nezahrnuje — uživatel ho stahuje zvlášť
- Stávající uživatelé dostanou notifikaci o aktualizaci při startu aplikace
- **Nekombinujte** variantu A a B pro stejnou verzi — jinak upload selže na duplicity
