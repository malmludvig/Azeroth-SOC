# Azeroth SOC

Ett personligt lärospel i nätverkssäkerhet. Du försvarar riktiga system i isolerade containrar med riktiga verktyg, och spelet dömer genom att skicka riktig trafik.

Designen finns i [docs/design.md](docs/design.md). Projektet är i steg 4 av 9: labbet, orkestreraren, målkontrollen, isoleringstesterna och Docker-proxyn finns, spelet är inte byggt ännu.

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

Skriv egna regler i porten och låt spelet döma dem:

```bash
dotnet run --project src/Game.Cli -- up
docker exec -it azl-<id>-gate bash      # skriv dina nftables-regler här
dotnet run --project src/Game.Cli -- check 1
```

Topologin i det fasta labbet:

```text
plains 10.10.0.0/24                          dmz 10.20.0.0/24
citizens .10   garrison .20                  web .10 (80, 22, 8080, 3306)
defias   .66   probe    .99
               \________ gate .2 | .2 ________/
```

All trafik mellan näten går genom `gate`. Näten har ingen väg ut.

## Om temat

Privat projekt för eget bruk, utan koppling till Blizzard Entertainment. World of Warcraft-namn förekommer bara som text; repot innehåller inga assets från Blizzards spel.
