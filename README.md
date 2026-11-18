# Azeroth SOC

Ett personligt lärospel i nätverkssäkerhet. Du försvarar riktiga system i isolerade containrar med riktiga verktyg, och spelet dömer genom att skicka riktig trafik.

Designen finns i [docs/design.md](docs/design.md). Projektet är i steg 8 av 9: scenariot Sentinel Hill går att spela i webbläsaren, med zonkarta, mätare, uppdragslogg och sparade framsteg.

## Prova labbet

Kräver Docker med Compose och bash. På Windows: Docker Desktop med WSL 2, och kör kommandona i en WSL-terminal med repot i WSL:s eget filsystem.

```bash
lab/up.sh        # bygg och starta
lab/verify.sh    # 34 kontroller: routing, brandvägg, rate limiting, tcpdump, isolering
docker exec -it azeroth-lab-gate-1 bash    # skal i porten
lab/down.sh      # stoppa och städa
```

Orkestreraren (steg 2) bygger samma labb med slumpade adresser och portar. Kräver .NET 10 SDK.

```bash
lab/build.sh
dotnet run --project src/Game.Cli -- up
dotnet run --project src/Game.Cli -- soak 10
dotnet run --project src/Game.Cli -- selftest
```

## Spela

```bash
lab/build.sh
dotnet run --project src/Game.Api
```

Öppna http://127.0.0.1:5080 och tryck på Start. Skriv `quest` i terminalen på sidan.

Vill du spela utan webbläsare:

```bash
dotnet run --project src/Game.Cli -- play
```

Terminalen blir spelledare och skriver ut ett `docker exec`-kommando. Kör det i en andra terminal för att kliva in i porten, och skriv `quest`.

 Därifrån sköter du allt: `quest` visar uppdraget, `quest hint` ger en ledtråd, `quest ready` ringer i klockan.

Topologin i det fasta labbet:

```text
plains 10.10.0.0/24                          dmz 10.20.0.0/24
citizens .10   garrison .20                  web .10 (80, 22, 8080, 3306)
defias   .66   probe    .99
               \________ gate .2 | .2 ________/
```

All trafik mellan näten går genom `gate`. Näten har ingen väg ut.

## Tema

Spelet har World of Warcraft-tema som standard. Byt till ett neutralt tema med en rad:

```bash
export AZEROTH_THEME=neutral
```

Temana ligger i `themes/`. Ett tema ändrar bara namn och text, aldrig vad spelaren ska göra.

## Om temat

Privat projekt för eget bruk, utan koppling till Blizzard Entertainment. World of Warcraft-namn förekommer bara som text; repot innehåller inga assets från Blizzards spel.
