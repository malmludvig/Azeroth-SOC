# Azeroth SOC

Ett lärospel i nätverkssäkerhet för en spelare. Spelaren försvarar riktiga system i isolerade containrar med riktiga verktyg, och spelet dömer genom att skicka riktig trafik. Hela designen finns i `docs/design.md`. Läs den innan du ändrar något större.

## Läget

Planen har nio steg (se "Repostruktur och plan för fas 2" i designen). Gör ett steg i taget och stanna när stegets "klart när" är uppfyllt.

- Steg 1, labbet för hand: klart. `lab/verify.sh` är kört på utvecklingsmaskinen (Windows 11, x86-64, Docker Desktop 29 med WSL 2, kärna 6.18) med macvlan-nät.
- Steg 2, orkestreraren: klart, utom socket-proxyn som flyttas till steg 4. `Game.Cli soak 10` är grön på utvecklingsmaskinen med de riktiga avbilderna och macvlan.
- Steg 3, målkontrollen: klart. `Game.Cli selftest` dömer 16 kända försvar rätt på utvecklingsmaskinen med riktig nginx och macvlan, två körningar i rad med jämna mätvärden. Våg 0 (spelarens rapport) har ingen kontroll ännu; den hör till `quest`-kommandot i steg 5.
- Steg 4, isoleringstester och socket-proxy: klart. `Game.Cli isolation` (49 kontroller) och `Game.Cli proxytest` (31 kontroller) är gröna på utvecklingsmaskinen med macvlan.
- Steg 5, scenariomotorn: klart. Spelat för hand på utvecklingsmaskinen, med `quest` i porten. `Game.Cli playtest` är grön mot Docker 29 på Linux.
- Steg 6, terminalgatewayen: klart. Webbterminalen fungerar i webbläsaren i Windows mot labbet i WSL. `Game.Cli termtest` är grön mot Docker 29 på Linux, direkt och genom proxyn.
- Steg 7, klienten: koden finns i `src/Game.Api/Components` (Blazor Server) och `src/Game.Core/GameHost.cs` (spelledaren i webbservern). `Game.Cli hosttest` är grön mot Docker 29 på Linux, direkt och genom proxyn, och sidan är kontrollerad i Chrome i alla lägen utom det interaktiva: Blazors `blazor.web.js` kommer från ett NuGet-paket som inte går att hämta i Claudes arbetsmiljö. Återstår att öppna sidan på utvecklingsmaskinen och spela från knappen Start.
- Steg 8, tema och historik: koden finns (`src/Game.Core/Theme.cs`, `History.cs`, `themes/`). `Game.Cli themetest` och `hosttest` är gröna mot Docker 29 på Linux, och sidan är kontrollerad i Chrome i båda temana. Återstår att spela på utvecklingsmaskinen och se framstegen sparas.
- Steg 9, flytt till Proxmox: inte påbörjat.

## Stack

- .NET 10. Backend: ASP.NET Core, SignalR och en egen tunn Docker-klient (`DockerEngine`). All spellogik ligger här.
- Klient: Blazor Server i `Game.Api`, med xterm.js för terminalen. Sidan ritar bara `GameView` från `GameHost` och startar eller stoppar spelet; den innehåller inga regler.
- Lagring: en JSON-fil, `~/.azeroth/history.json` (eller `AZEROTH_HOME`).
- Scenarion: JSON i `lab/scenarios/`. Teman: `themes/theme-<namn>.json`, valt med `AZEROTH_THEME` (standard `wow`, `neutral` finns).

## Kommandon

```bash
lab/up.sh       # bygg och starta labbet, lägg till rutterna genom gate
lab/verify.sh   # kontrollera routing, nftables, rate limiting, tcpdump, isolering
lab/down.sh     # stoppa och ta bort labbet
docker exec -it azeroth-lab-gate-1 bash   # spelarens skal
```

Skripten ovan kör det fasta labbet från steg 1. Orkestreraren kör slumpade körningar:

```bash
lab/build.sh                                  # bygg avbilderna
dotnet run --project src/Game.Cli -- up       # ny körning (--seed N, --replace)
dotnet run --project src/Game.Cli -- status
dotnet run --project src/Game.Cli -- reset    # samma frö, nytt tillstånd
dotnet run --project src/Game.Cli -- down
dotnet run --project src/Game.Cli -- sweep    # ta bort alla körningar (--expired: bara utgångna)
dotnet run --project src/Game.Cli -- soak 10  # test av steg 2: tio varv utan kvarlämnade resurser
dotnet run --project src/Game.Cli -- check 1  # döm den pågående körningen mot våg 1, 2 eller 3
dotnet run --project src/Game.Cli -- selftest # test av steg 3: kända rätta och felaktiga försvar, tar drygt tre minuter
dotnet run --project src/Game.Cli -- play     # spela: den här terminalen är spelledare, spelaren sitter i gate och skriver quest
dotnet run --project src/Game.Cli -- playtest # test av steg 5: automatisk genomspelning med fel försök och ledtrådar
dotnet run --project src/Game.Api             # spelet i webbläsaren på http://127.0.0.1:5080 (egen terminal; --start [--wave N] startar direkt)
dotnet run --project src/Game.Cli -- hosttest # test av steg 7: spelledaren bakom sidan, med quest i porten (inte samtidigt som Game.Api spelar)
dotnet run --project src/Game.Cli -- history  # framsteg per våg över alla spel
dotnet run --project src/Game.Cli -- themetest # test av steg 8: alla teman laddas och inget ändrar vad spelaren ska göra
export AZEROTH_THEME=neutral                  # byt tema (Game.Api och Game.Cli; Game.Api tar också --theme <namn>)
dotnet run --project src/Game.Cli -- termtest # test av steg 6: använder webbterminalen som webbläsaren gör (kräver Game.Api)
dotnet run --project src/Game.Cli -- isolation # test av steg 4: försök ta sig ut ur labbet från varje container
dotnet run --project src/Game.DockerProxy     # starta Docker-proxyn (egen terminal)
dotnet run --project src/Game.Cli -- proxytest # test av steg 4: förbjudna anrop mot proxyn ska nekas
export DOCKER_HOST=unix://$HOME/.azeroth/docker-proxy.sock   # kör Game.Cli genom proxyn
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
- Angreppsverktyg finns bara i labbavbilderna och riktas bara mot labbets adressrymd: 10.10.0.0/24 och 10.20.0.0/24 i det fasta labbet, 10.77.0.0/16 i orkestrerade körningar.
- Backend ska nå Docker via en socket-proxy med vitlista och lyssna bara på localhost.

## Beslut som avviker från eller preciserar designen

- **Nätdrivrutin: macvlan, inte bridge.** På en Docker-bridge med `internal: true` släpper värdens brandvägg bara igenom paket där både käll- och måladress ligger i bryggans eget subnät, när värden filtrerar bryggad trafik. Routad trafik genom `gate` har alltid en adress utanför subnätet och tappas. Uppmätt på Docker 29, Linux. `lab/compose.yaml` använder därför macvlan på en dummy-förälder; `LAB_NET_DRIVER=bridge` är en reservväg och `lab/up.sh` faller tillbaka på den automatiskt.
- **`gate` har `NET_RAW` utöver `NET_ADMIN`.** tcpdump och ping kräver det. Designen säger "`NET_ADMIN` och inget mer". Kör tcpdump utan `-Z`: flaggan får tcpdump att byta användare, vilket kräver capabilities som `gate` saknar.
- **Rutterna läggs till från värden** med `docker exec --privileged ... ip route` i `lab/up.sh`. Då behöver ingen annan container än `gate` `NET_ADMIN`. Orkestreraren i steg 2 ska göra samma sak.
- **Egen Docker-klient i stället för Docker.DotNet.** `DockerEngine` talar direkt med Docker Engine API över socketen och har bara de anrop labbet behöver. Listan över anrop är densamma som en socket-proxy ska släppa igenom. Lägg inte till anrop utan att fråga.
- **Docker-proxyn är egen kod** (`src/Game.DockerProxy`), inte en färdig avbild. Den släpper igenom exakt de anrop `DockerEngine` gör och granskar innehållet: bara labbets avbilder, namn, etiketter och adressrymd, inga monteringar, inga okända inställningar, och privilegierad exec bara för `ip route replace` inom adressrymden. Allt som inte står på listan nekas, så ett nytt anrop i `DockerEngine` kräver en ny regel i `Policy.cs` och ett nytt fall i `ProxyTest.cs`.
- **Proxyn skyddar först när backend saknar egen åtkomst till Docker.** På utvecklingsmaskinen kör allt som samma användare, och proxyn är där frivillig. I driftsättningen (steg 9) ska backend köra som en användare utanför `docker`-gruppen med `DOCKER_HOST` satt till proxyns socket.
- **Bridge-nät klarar inte isoleringstestet.** På en bridge sitter Docker-värden på `.1` i varje labbnät. Reservvägen `LAB_NET_DRIVER=bridge` är därför bara till för felsökning.
- **All status ligger i etiketter** på containrar och nät (`azeroth.lab`, `azeroth.run`, `azeroth.seed`, `azeroth.expires`). En körning är en ren funktion av scenario och frö och byggs om från etiketterna. Det finns ingen statusfil.
- **Testerna är kommandon i `Game.Cli`** (`soak`, `selftest`), inte xunit-projekt. Nya kontroller ska få sina rätta och felaktiga försvar i `SelfTest.cs`.
- **Floden i våg 3 kommer från två värdar i tur och ordning** (`defias`, sedan `probe`). Det ersätter designens adressbyte halvvägs och ger samma lektion: en spärrlista med en adress håller inte. Takten räknas i webbserverns logg efter den första sekunden, så att ett burst-utrymme inte räknas mot försvaret.
- **Ingen kontroll får bero på väggklockan.** Klockan i WSL 2 kan gå fel med tiotals minuter och justeras i hopp efter att Windows legat i vila. En flod som pågick till ett klockslag tog då slut för tidigt och gav för låga mätvärden. Trafikskript räknar anrop, C#-koden mäter med `Stopwatch`, och en mätning som inte blev som planerat döms som underkänd (`INCONCLUSIVE`), aldrig som godkänd.
- **Scenariot är JSON, inte YAML.** `lab/scenarios/sentinel-hill.json` bestämmer vågor, vilka kontroller varje våg använder och med vilka tal, ledtrådar och all text spelaren ser. Kontrollernas kod ligger i `Game.Checks` och topologin i `SentinelHill.cs`. Textfält är listor av rader.
- **Scenariotexten är neutral och på engelska.** Den säger "the notice board" och "the garrison", inga WoW-namn. Allt WoW ligger i `themes/theme-wow.json`.
- **Ett tema ersätter bara text:** scenariots titel, intro och outro, vågornas titlar, uppdrag och frågor, kontrollernas titlar, och namnen sidan visar (`Theme.NeutralNames` listar nycklarna). Vad en våg kontrollerar och ledtrådarna ändras aldrig. `Theme.Apply` nekar ett uppdrag som tappar en `{platshållare}` eller en rad med ett `quest`-kommando ur originalet, så att ett tema inte kan ändra vad spelaren ska göra. Domens detaljrader använder rollnamnen (`defias`, `garrison`) oavsett tema.
- **Temat väljs vid start.** Ett byte kräver omstart av `Game.Api`.
- **`quest` talar med spelet genom filer i `/run/quest` i `gate`.** Porten har ingen nätväg till spelet. `quest` lämnar en begäran, spelledaren hämtar den utifrån med `docker exec` och lämnar ett svar. Inget spelet avgör vilar på de filerna: rapporten jämförs med facit och vågor döms av trafik.
- **En våg är själva domen.** Trafiken i en våg är kontrollernas trafik, 10 till 25 sekunder. Det finns ingen separat fas där spelaren justerar under pågående attack; spelaren justerar och ringer i klockan igen. Genomgången efter en våg är kontrollernas utfall och portens läge, inte en tidslinje från regelräknare.
- **Poäng**: vågens grundpoäng, minus 20 per ledtrådsnivå och 10 per misslyckat försök, lägst 10. En våg är bemästrad om den hölls utan ledtråd.
- **Historiken är en JSON-fil, inte SQLite.** NuGet är spärrat i Claudes arbetsmiljö, och för en spelare räcker en fil som läses en gång och skrivs om vid varje ändring. `GameRecorder` sparar varje spel medan det spelas: försök, vilka kontroller som föll (som id, så att varje tema kan namnge dem), ledtrådar och hållna vågor. Framstegen räknas per våg och färdighet (`skill` i scenariot), och spelet föreslår att börja på den första våg som inte är bemästrad.
- **Webbterminalen är ett skal per körning som lever kvar på servern.** Webbläsare kopplar till och från; de senaste 64 kB utdata spelas upp igen vid återanslutning, så en omladdning ger samma prompt och historik. Lämnar spelaren skalet (`exit`) startas ett nytt vid nästa anslutning.
- **`Game.Api` lyssnar bara på 127.0.0.1 och kräver samma Origin på WebSocket-anslutningen.** Terminalen är ett root-skal i `gate`, så en sida från en annan webbplats får inte kunna öppna den genom spelarens webbläsare.
- **Terminalen startas utan innehåll i begäran.** Kestrel, och därmed proxyn, nekar innehåll i en upgrade-begäran. Utan innehåll ramar Docker in utdata som för ett kommando utan terminal, och `TerminalConnection.ReadAsync` packar upp ramarna. Proxyn tillåter en terminal bara som `bash` i `gate`, utan privilegier, och bara upgrade för execs den själv skapat som terminaler.
- **Klienten är Blazor Server, inte TypeScript och Phaser.** Spelledaren (`GameHost`) körs i webbservern och komponenterna läser dess `GameView` direkt. Zonkartan är SVG i en Razor-komponent. Terminalen är xterm.js, kopierat in i `wwwroot/vendor` (MIT, oförändrat från npm), och startas av `GateTerminal.razor` via `terminal.js`; tangenttryckningarna går över `/terminal`, inte genom Blazors anslutning.
- **Bara en spelledare åt gången.** `Game.Cli play` och knappen Start i `Game.Api` är två spelledare som läser samma `quest`-filer. Kör inte båda mot samma labb.
- **Kartan är pixelgrafik i SVG**, 300 gånger 104 bildpunkter, egenritad. `PixelArt.cs` ritar det som står still en gång (himmel, åkrar, kvarn, mur, stad, hallen och folket på slätten) och `ZoneMap.razor` det som ändras: fällgallret (nere när alla dörrar som ska vara stängda är det), dörrarna med lampor, och trafiken under en våg, som vänder vid muren eller går igenom efter hur dörrarna senast mättes. Figurerna är allmänna fantasyfigurer, inte figurer från WoW.
- **Dörrarna på kartan mäts levande** var tredje sekund mellan vågorna: kan `defias` nå varje port, och kan `garrison` nå förvaltningsporten. Inte under en våg, så att mätningarna inte störs, och inte i våg 0, där portarna är det spelaren ska ta reda på.
- **Mätarna kommer från domens tal.** `CheckResult.Measures` bär `citizen-rate`, `forbidden-reached` och `worst-rate`. Stadens hälsa är den lägsta andelen betjänade invånare, intrång är antalet förbjudna dörrar som nåddes plus källor som flödade över gränsen.
- **Statiska filer serveras med `UseStaticFiles` och `UseStaticWebAssets`, inte `MapStaticAssets`.** Körd direkt från byggmappen svarade `MapStaticAssets` med tomma filer när webbläsaren bad om komprimerade varianter.
- **Blazor-paketet saknas i Claudes arbetsmiljö.** Där bygger `Game.Api` med en lokal `Directory.Build.targets` utanför repot som tar bort `Microsoft.AspNetCore.App.Internal.Assets`. Den ska aldrig in i repot; på utvecklingsmaskinen hämtas paketet från NuGet.
- **En körning stannar när Docker startas om** och startar inte av sig själv (`RestartPolicy` är `no`). `LabRun.IsRunning` är falsk då; `status` säger det, `play` bygger om körningen med samma frö, och webbsidan visar att labbet är stoppat i stället för att försöka öppna en terminal.
- **En delad avbild för värdarna på slätten** (`lab/images/host`). Trafiken skickas med `docker exec` från `Game.Checks`.

## Konventioner

- Dokumentation på svenska. Kod, kommentarer, identifierare och skriptutskrifter på engelska.
- Efter `git pull` på utvecklingsmaskinen: bygg med `dotnet build --no-incremental`. När klockan i WSL går fel hoppar det vanliga bygget över ändrade filer utan varning.
- Utvecklingsmaskinen är Windows 11. Repot ska ligga i WSL 2:s eget filsystem (inte under `/mnt/c`) och alla skript körs i en WSL-terminal.
- Skalskript skrivs för bash och ska ha LF-radslut (`.gitattributes` ser till det).
- Containrar heter `azeroth-lab-<roll>-1` i det fasta labbet och `azl-<körnings-id>-<roll>` i orkestrerade körningar. Roller: `gate`, `web`, `citizens`, `garrison`, `defias`, `probe`.
