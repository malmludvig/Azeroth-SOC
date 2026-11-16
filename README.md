# Azeroth SOC

Ett personligt lärospel i nätverkssäkerhet. Du försvarar riktiga system i isolerade containrar med riktiga verktyg, och spelet dömer genom att skicka riktig trafik.

Designen finns i [docs/design.md](docs/design.md). Projektet är i steg 1 av 9: labbet finns, spelet är inte byggt ännu.

## Prova labbet

Kräver Docker med Compose.

```bash
lab/up.sh        # bygg och starta
lab/verify.sh    # 34 kontroller: routing, brandvägg, rate limiting, tcpdump, isolering
docker exec -it azeroth-lab-gate-1 bash    # skal i porten
lab/down.sh      # stoppa och städa
```

Topologin:

```text
plains 10.10.0.0/24                          dmz 10.20.0.0/24
citizens .10   garrison .20                  web .10 (80, 22, 8080, 3306)
defias   .66   probe    .99
               \________ gate .2 | .2 ________/
```

All trafik mellan näten går genom `gate`. Näten har ingen väg ut.

## Om temat

Privat projekt för eget bruk, utan koppling till Blizzard Entertainment. World of Warcraft-namn förekommer bara som text; repot innehåller inga assets från Blizzards spel.
