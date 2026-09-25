# LogisticsRoutes

## Pornire locală (PowerShell)

Din rădăcina repository-ului, PostgreSQL:

1. Copiază exemplul: `Copy-Item .env.example .env`.
2. Completează `POSTGRES_PASSWORD` în `.env`; poți schimba și numele bazei sau utilizatorul. Fișierul `.env` este ignorat de Git.
3. Pornește baza: `docker compose up -d --wait`. Verifică starea cu `docker compose ps`.
4. Oprește baza: `docker compose down`. Volumul numit păstrează datele între porniri.

Într-un terminal separat, pornește API-ul. Înlocuiește valorile dintre paranteze cu cele din `.env` și cu coordonatele reale ale depozitului. Baza folosește portul local `5433`.

```powershell
$env:ConnectionStrings__LogisticsDb = 'Host=127.0.0.1;Port=5433;Database=<POSTGRES_DB>;Username=<POSTGRES_USER>;Password=<POSTGRES_PASSWORD>'
$env:Planning__DepotLatitude = '<latitudine_depozit>'
$env:Planning__DepotLongitude = '<longitudine_depozit>'
dotnet ef database update --project LogisticsRoutes.BusinessLayer --startup-project LogisticsRoutes.API
dotnet run --project LogisticsRoutes.API --launch-profile http
```

Migrarea se execută o singură dată pentru o bază nouă; `dotnet ef` necesită unealta `dotnet-ef` instalată. API-ul HTTP ascultă pe `http://localhost:5212`.

În al treilea terminal, pornește frontendul:

```powershell
cd frontend
npm.cmd install
npm.cmd run dev
```

Deschide adresa afișată de Vite (implicit `http://localhost:5173`). Cererile `/api` sunt trimise prin proxy către API-ul local. Dacă API-ul rulează pe altă adresă, setează `API_PROXY_TARGET` în terminalul frontendului înainte de `npm.cmd run dev`, de exemplu `$env:API_PROXY_TARGET = 'http://localhost:5212'`. Frontendul folosește numai datele returnate de API.

## Traseu rutier pe hartă

Proiectul are un serviciu OSRM local separat de PostgreSQL în `compose.osrm.yaml`. Folosește extractul [OpenStreetMap Moldova de la Geofabrik](https://download.geofabrik.de/europe/moldova.html) și profilul auto OSRM. Din rădăcina repository-ului, pregătește datele o singură dată (sau repetă pașii după o actualizare a extractului):

```powershell
New-Item -ItemType Directory -Force data/osrm | Out-Null
curl.exe --fail --location --retry 3 --continue-at - --output data/osrm/moldova-latest.osm.pbf https://download.geofabrik.de/europe/moldova-latest.osm.pbf
curl.exe --fail --location --retry 3 --output data/osrm/moldova-latest.osm.pbf.md5 https://download.geofabrik.de/europe/moldova-latest.osm.pbf.md5
$expected = (Get-Content data/osrm/moldova-latest.osm.pbf.md5 -Raw).Trim().Split(' ')[0].ToLowerInvariant()
$actual = (Get-FileHash data/osrm/moldova-latest.osm.pbf -Algorithm MD5).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw 'Extractul Moldova nu corespunde sumei MD5 publicate.' }

$osrmData = (Resolve-Path data/osrm).Path
$osrmImage = 'ghcr.io/project-osrm/osrm-backend@sha256:8a1b1bc938412f15f9b5b32d794c4ec6bf4a85dfbbabfa0a014b70b187edb53b'
docker pull $osrmImage
docker run --rm --mount "type=bind,source=$osrmData,target=/data" $osrmImage osrm-extract -p /opt/car.lua /data/moldova-latest.osm.pbf
docker run --rm --mount "type=bind,source=$osrmData,target=/data" $osrmImage osrm-partition /data/moldova-latest.osrm
docker run --rm --mount "type=bind,source=$osrmData,target=/data" $osrmImage osrm-customize /data/moldova-latest.osrm
docker compose -f compose.osrm.yaml up -d --wait
```

Pregătirea folosește pașii [recomandați de OSRM](https://github.com/Project-OSRM/osrm-backend#using-docker) pentru algoritmul MLD. Fișierul `.osm.pbf` și fișierele `.osrm.*` rămân în `data/osrm`, ignorat de Git. Când actualizezi extractul, oprește mai întâi OSRM (`docker compose -f compose.osrm.yaml stop osrm`), refă cei trei pași de pregătire și pornește-l cu `docker compose -f compose.osrm.yaml start osrm`. Compose-ul OSRM folosește un proiect Docker separat și portul local `127.0.0.1:5000`; nu modifică serviciul PostgreSQL din `compose.yaml`.

Configurează frontendul să folosească serviciul local:

```powershell
Copy-Item frontend/.env.example frontend/.env.local
cd frontend
npm.cmd run dev
```

Exemplul setează `VITE_OSRM_BASE_URL=http://127.0.0.1:5000`, fără `/route/v1/driving`. Frontendul cere ruta prin API-ul OSRM Route cu coordonatele `longitudine,latitudine` în ordinea opririlor, `overview=full&geometries=geojson&steps=false`. Nu folosește serviciul Trip, deci nu optimizează ordinea. Adresa este inclusă în codul frontend la build; seteaz-o înainte de `npm.cmd run build` pentru distribuție. Serviciul trebuie să fie accesibil din browser și să permită CORS pentru originea frontendului; o pagină HTTPS necesită și un serviciu HTTPS.

Harta păstrează atribuirea OpenStreetMap pentru plăcile afișate. Traseul acoperă numai opririle rutei, fără segmentul de la sau către depozit. Distanța și durata provin din profilul și datele serviciului de rutare; durata este estimativă și nu stabilește ora sosirii. Acoperirea geografică, disponibilitatea și limitele de cereri depind de serviciul configurat. Când acesta lipsește, răspunde cu eroare sau nu găsește drum, harta păstrează marcajele și afișează o linie schematică. Pentru trafic sau utilizare susținută, configurează un serviciu administrat ori o instanță proprie, cu limite potrivite aplicației. Formatul cererii și răspunsului este descris în [documentația OSRM Route](https://project-osrm.org/docs/v5.22.0/api/#route-service).

## Urmărirea poziției vehiculului (prima etapă)

După migrarea bazei (`dotnet ef database update --project LogisticsRoutes.BusinessLayer --startup-project LogisticsRoutes.API`), API-ul acceptă `POST /api/routes/{routeId}/position` cu `latitude`, `longitude`, `reportedAt` (ISO 8601 cu fus orar) și `source` (`Reported` sau `Simulated`). `GET` pe aceeași adresă întoarce ultima poziție, `204` dacă ruta există dar nu are raportări și `404` pentru ruta inexistentă. Coordonatele din afara intervalelor latitudine −90…90 și longitudine −180…180, ora lipsă sau cu peste 5 minute în viitor și sursa necunoscută primesc `400`. O raportare mai veche decât ultima salvată primește `409`. Sunt salvate separat momentul raportării și momentul primirii. Raportarea nu schimbă opririle sau comenzile.

Dispecerul citește poziția rutei selectate la fiecare 3 secunde, fără reîncărcarea paginii. Marcajul vehiculului este distinct de opriri. Interfața arată momentul ultimei primiri și marchează datele drept vechi când ora raportării este cu peste 60 de secunde în urmă; arată și absența sau indisponibilitatea poziției. O poziție cu `source: "Simulated"` este etichetată vizibil ca **simulată, nu GPS real**. Sursa `Reported` indică doar o raportare către API; această etapă nu autentifică dispozitivul și nu oferă ETA sau urmărire GPS verificată.

Pentru a vedea marcajul mișcându-se pe străzi în dezvoltare, pornește API-ul, frontendul și OSRM local, apoi alege o rută de test cu minimum două opriri. Copiază ID-ul rutei din `GET http://localhost:5212/api/routes?day=YYYY-MM-DD` și rulează din rădăcina repository-ului:

```powershell
./scripts/dev/simulate-route-position.ps1 -RouteId '<ID-ul-rutei>' -IntervalSeconds 2
```

Simulatorul acceptă numai API și OSRM locale (`localhost` sau `127.0.0.1`). Cere de la OSRM geometria rutieră a opririlor în ordinea `Sequence`, exact ca harta, și trimite poziții `Simulated` la distanțe egale de-a lungul acesteia, la intervalul ales. Pentru închiderea ciclului, marcajul revine pe aceeași linie rutieră, în sens invers. Repetă ciclul până la `Ctrl+C`; `-Cycles 1` execută un singur ciclu. Pentru alte porturi locale folosește `-ApiBaseUrl 'http://127.0.0.1:<port>'` și/sau `-OsrmBaseUrl 'http://127.0.0.1:<port>'`. `-StepsPerLeg` stabilește numărul de raportări pentru fiecare oprire din ciclu. Dacă OSRM nu răspunde sau nu găsește traseu, simulatorul afișează motivul și trece explicit la **FALLBACK LINIE DREAPTĂ**, cu interpolarea inițială între opriri. Pozițiile rămân artificiale și nu reprezintă poziția reală a vehiculului. Simulatorul nu trimite coordonatele comenzilor către un serviciu public.

## Planificarea rutelor

Înainte de `POST /api/routes/plan`, configurează coordonatele **depozitului tău** prin `Planning:DepotLatitude` și `Planning:DepotLongitude`. Nu există coordonate implicite.

Latitudinea trebuie să fie între −90 și 90, iar longitudinea între −180 și 180. Poți seta aceleași chei și în configurația locală ASP.NET Core, sub secțiunea `Planning`.

Cererea conține ziua de planificare: `{"day":"YYYY-MM-DD"}`. Serviciul include doar comenzile `Confirmed` din acea zi, păstrează fiecare rută într-o singură zonă, respectă capacitatea vehiculului și atribuie un vehicul și un șofer distinct fiecărei rute. Opririle sunt ordonate prin nearest neighbor cu distanța Haversine de la depozit. Ora estimată a sosirii rămâne necompletată până când există date pentru o estimare reală. Dacă nu sunt comenzi confirmate, răspunsul conține `routes: []`; dacă planificarea nu poate fi completată sau există deja rute în acea zi, API-ul răspunde cu `409` fără să salveze o planificare parțială.

## Starea unei opriri

`PATCH /api/routes/{routeId}/stops/{stopId}/status` primește un corp JSON precum `{"status":"Departed"}`. Stările permise urmează `Pending → Departed → Arrived → Delivered / Refused / PartialReturn`; ultimele trei sunt finale în acest MVP. API-ul întoarce `400` pentru un nume de status necunoscut, `404` dacă ruta sau oprirea nu există ori oprirea nu aparține rutei, și `409` pentru o tranziție nepermisă. La `Delivered`, comanda asociată devine tot `Delivered`; la `Refused` sau `PartialReturn`, comanda rămâne `Planned` până la definirea unui flux ulterior.
