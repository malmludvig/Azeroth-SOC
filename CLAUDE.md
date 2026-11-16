# Azeroth SOC

Ett lärospel i nätverkssäkerhet för en spelare. Spelaren försvarar riktiga system i isolerade containrar med riktiga verktyg, och spelet dömer genom att skicka riktig trafik. Hela designen finns i `docs/design.md`. Läs den innan du ändrar något större.

## Läget

Planen har nio steg (se "Repostruktur och plan för fas 2" i designen). Gör ett steg i taget och stanna när stegets "klart när" är uppfyllt.

- Steg 1, labbet för hand: filerna finns i `lab/`. Skriptlogiken är körd mot Docker 29 på Linux. Återstår att köra `lab/up.sh` och `lab/verify.sh` på utvecklingsmaskinen (Windows 11, x86-64, Docker Desktop med WSL 2).
- Steg 2 och framåt: inte påbörjade. Ingen .NET-kod finns ännu.

## Stack

- Backend: ASP.NET Core, SignalR, Docker.DotNet. All spellogik ligger här.
- Klient: TypeScript, Phaser 3, xterm.js. Tunn, innehåller inga regler.
- Lagring: SQLite.
- Scenarion: YAML i `lab/scenarios/`. Tema: `themes/theme-wow.json`.

## Kommandon

```bash
lab/up.sh       # bygg och starta labbet, lägg till rutterna genom gate
lab/verify.sh   # kontrollera routing, nftables, rate limiting, tcpdump, isolering
lab/down.sh     # stoppa och ta bort labbet
docker exec -it azeroth-lab-gate-1 bash   # spelarens skal
```

## Principer

- **Döm beteende, inte syntax.** Godkänt avgörs av probtrafik och mätning, aldrig av hur en regel är skriven. Tillståndsläsning (`nft -j list ruleset`) används bara för återkoppling och ledtrådar.
- **Betygsgrundande mätvärden kommer från containrar spelaren inte har skal i.** Spelaren är root i `gate`.
- **Varje kontroll har tester mot riktiga containrar:** minst en rätt lösning som godkänns och tre felaktiga som underkänns.
- **Tema och logik är åtskilda.** Inga WoW-namn i kod, scenariologik eller containernamn utöver det som redan finns; namn, dialog och lore hör hemma i temafilen. Inga assets från Blizzards spel i repot.
- **Fokus är försvar.** Angreppstrafiken är skriptad med standardverktyg mot labbets egna mål.

## Säkerhetskrav för labbet

Dessa är krav, inte riktlinjer. Ändra dem inte utan att fråga.

- Labbnäten är `internal: true`. Ingen labbcontainer får nå internet, hemnätet eller Docker-socketen. `lab/verify.sh` kontrollerar det.
- Bara `gate` har capabilities: `NET_ADMIN` och `NET_RAW`. Värdarna på slätten har inga. Aldrig `privileged: true` på en container.
- `no-new-privileges` och gränser för CPU, minne och antal processer på alla containrar.
- Angreppsverktyg finns bara i labbavbilderna och riktas bara mot labbets adressrymd (10.10.0.0/24 och 10.20.0.0/24).
- Backend ska nå Docker via en socket-proxy med vitlista och lyssna bara på localhost.

## Beslut som avviker från eller preciserar designen

- **Nätdrivrutin: macvlan, inte bridge.** På en Docker-bridge med `internal: true` släpper värdens brandvägg bara igenom paket där både käll- och måladress ligger i bryggans eget subnät, när värden filtrerar bryggad trafik. Routad trafik genom `gate` har alltid en adress utanför subnätet och tappas. Uppmätt på Docker 29, Linux. `lab/compose.yaml` använder därför macvlan på en dummy-förälder; `LAB_NET_DRIVER=bridge` är en reservväg och `lab/up.sh` faller tillbaka på den automatiskt.
- **`gate` har `NET_RAW` utöver `NET_ADMIN`.** tcpdump och ping kräver det. Designen säger "`NET_ADMIN` och inget mer".
- **Rutterna läggs till från värden** med `docker exec --privileged ... ip route` i `lab/up.sh`. Då behöver ingen annan container än `gate` `NET_ADMIN`. Orkestreraren i steg 2 ska göra samma sak.
- **En delad avbild för värdarna på slätten** (`lab/images/host`) tills trafikskripten kommer i steg 5.

## Konventioner

- Dokumentation på svenska. Kod, kommentarer, identifierare och skriptutskrifter på engelska.
- Utvecklingsmaskinen är Windows 11. Repot ska ligga i WSL 2:s eget filsystem (inte under `/mnt/c`) och alla skript körs i en WSL-terminal.
- Skalskript skrivs för bash och ska ha LF-radslut (`.gitattributes` ser till det).
- Containrar heter `azeroth-lab-<roll>-1`. Roller: `gate`, `web`, `citizens`, `garrison`, `defias`, `probe`.
