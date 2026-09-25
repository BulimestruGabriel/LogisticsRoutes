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

Rulează `dotnet ef database update` și pe o bază existentă când apar migrații noi; EF aplică numai migrațiile încă lipsă. `dotnet ef` necesită unealta `dotnet-ef` instalată. API-ul HTTP ascultă pe `http://localhost:5212`.

În al treilea terminal, pornește frontendul:

```powershell
cd frontend
npm.cmd install
npm.cmd run dev
```

Deschide adresa afișată de Vite (implicit `http://localhost:5173`). Cererile `/api` sunt trimise prin proxy către API-ul local. Dacă API-ul rulează pe altă adresă, setează `API_PROXY_TARGET` în terminalul frontendului înainte de `npm.cmd run dev`, de exemplu `$env:API_PROXY_TARGET = 'http://localhost:5212'`. Frontendul folosește numai datele returnate de API.

## Copiază comenzile de ieri

Rulează migrarea `AddCopiedOrderProvenance` cu comanda `dotnet ef database update` de mai sus înainte de folosirea funcției. În Dispecer, alege ziua livrării, apasă **Previzualizează comenzile de ieri**, verifică lista și selecția, apoi **Creează comenzi New**. Sunt oferite comenzile din ziua calendaristică precedentă cu status `Confirmed`, `Planned` sau `Delivered`. O oprire `Refused` ori `PartialReturn` primește avertisment și rămâne deselectată implicit; dispecerul o poate selecta după verificare. O comandă deja copiată din aceeași sursă pentru ziua aleasă este afișată, dar nu mai poate fi selectată. Dacă în ziua aleasă există comenzi cu aceeași zonă și adresă (comparare fără diferențe de litere și spații), previzualizarea le semnalează **doar ca posibile potriviri**: nu le modifică și nu exclude automat sursa. Adresa nu este un identificator sigur de magazin; înainte de copiere, dispecerul trebuie să compare manual comenzile, iar un magazin existent astăzi poate primi încă o comandă dacă aceasta este intenția.

API: `GET /api/orders/copy-yesterday/preview?day=YYYY-MM-DD` întoarce `day`, `sourceDay` și `candidates` cu `sourceOrder`, `deliveryStatus`, `warnDeliveryOutcome`, `alreadyCopiedOrderId` și `possibleExistingToday`. `POST /api/orders/copy-yesterday` primește `{ "day": "YYYY-MM-DD", "sourceOrderIds": ["..."] }` și întoarce separat `created` și `alreadyCopied`. Serverul revalidează ziua și statusurile surselor la salvare; o selecție devenită invalidă primește `409` fără copii parțiale. Cererea repetată întoarce copiile existente. Coloana `SourceOrderId` păstrează proveniența și indexul unic `(SourceOrderId, DeliveryDate)` împiedică dublurile inclusiv între cereri simultane. Copia primește ID nou, ziua selectată și status `New`; zona, adresa, coordonatele și volumul sunt preluate din comanda-sursă la momentul copierii. Rutele, opririle, ordinea și statusurile de livrare nu sunt copiate și comenzile de ieri nu se modifică.

După copiere, comenzile apar în lista zilei cu mențiunea „Copiată din comanda de ieri”. Corectează volumul cu **Salvează volum** cât statusul este `New`, apoi apasă **Confirmă** (acțiunea existentă). `PATCH /api/orders/{id}/volume` primește `{ "volume": 3.125 }`; cere un volum pozitiv cu maximum trei zecimale și răspunde `409` dacă între timp comanda nu mai este `New`. Această versiune nu are `StoreId` și nu deduce identitatea magazinului din adresă. Verificarea potrivirilor este orientativă, iar schimbările sursei după copiere nu actualizează automat copia. API-ul de administrare a comenzilor rămâne disponibil în aceeași rețea de încredere ca Dispecerul; nu este un control de acces per utilizator.

### Capacitate la confirmare și comenzi apărute după planificare

Editorul de volum pentru o comandă `New` arată un avertisment când valoarea introdusă depășește capacitatea maximă a **unui singur vehicul** din flotă. Volumul trebuie salvat înainte de confirmare. API-ul verifică din nou flota chiar în operația `PATCH /api/orders/{id}/confirm`: dacă între editare și confirmare capacitatea disponibilă s-a schimbat, refuză cu `409` și un mesaj care indică volumul și capacitatea maximă actuală. În lipsa unui vehicul, confirmarea este refuzată. Această verificare stabilește doar dacă o comandă poate fi transportată singură; atribuirea la o rută depinde separat de locul liber, zonă, șofer și vehicul nefolosite în ziua respectivă.

După ce există rute, Dispecerul afișează **Planifică comenzile rămase**. Contorul **De planificat** arată comenzile `Confirmed` cu o acțiune disponibilă pe baza ultimelor date: adăugare la o rută neîncepută din aceeași zonă, cu loc, sau creare de rută nouă cu vehicul și șofer liberi. Comenzile confirmate blocate sunt numărate separat și afișează motivul (de exemplu, volum peste maxim, lipsa unui vehicul potrivit sau a unui șofer liber). Pentru o singură comandă fără rută compatibilă, se poate apăsa **Creează rută nouă pentru această comandă**; pentru rutele compatibile rămâne **Adaugă la rută**. Flota se recitește periodic și după adăugarea unui vehicul sau șofer; API-ul revalidează întotdeauna datele înainte de salvare.

Dacă o comandă este deja `Confirmed`, dar volumul ei depășește capacitatea maximă actuală a unui vehicul, Dispecerul arată motivul blocării și butonul **Corectează volumul**. Corectarea este explicită; afișarea sau planificarea nu schimbă singure comanda. Introdu un volum pozitiv cu cel mult trei zecimale, care încape în cel puțin un vehicul, și apasă **Salvează corectarea**. `PATCH /api/orders/{id}/confirmed-volume` primește `{ "volume": 5, "expectedVolume": 13 }`. API-ul acceptă operația numai dacă statusul este încă `Confirmed`, comanda nu are nicio oprire într-o rută, volumul este încă cel citit la deschiderea editorului și flota actuală poate transporta noul volum într-un singur vehicul. Răspunsul `409` explică schimbarea concurentă, planificarea între citire și salvare sau capacitatea insuficientă; Dispecerul reîncarcă listele. După succes, comanda rămâne `Confirmed` și poate fi adăugată la o rută compatibilă ori planificată într-o rută nouă. `PATCH /api/orders/{id}/volume` rămâne rezervat comenzilor `New`. Corectarea nu modifică opriri, rute sau statusuri și nu face automat nicio schimbare în comenzile existente.

`POST /api/routes/plan-remaining` primește `{ "day": "YYYY-MM-DD", "orderIds": ["..."] }` și întoarce `createdRoutes` și `extendedRoutes`. `orderIds` selectează comenzile confirmate de planificat; fără această listă, API-ul încearcă toate comenzile confirmate ale zilei. Opririle existente își păstrează ID-ul, statusul și `Sequence`; opririle noi sunt adăugate la finalul unei rute compatibile. Pentru comenzile care nu încap pe o astfel de rută, serviciul creează rute separate pe zone folosind numai vehicule și șoferi nefolosiți în acea zi. Toată cererea rulează într-o singură tranzacție serializabilă: dacă atribuirea sau salvarea eșuează, nu rămân rute, opriri ori schimbări de status parțiale. Cererile concurente pot primi `409` și trebuie reluate după reîncărcarea datelor. Migrarea `UniqueDailyRouteResources` adaugă unicitate pentru `(Date, VehicleId)` și `(Date, DriverId)`; aplic-o cu `dotnet ef database update` înainte de pornirea noii versiuni. Endpointul vechi `POST /api/routes/plan` rămâne pentru planificarea inițială și continuă să refuze zilele care au deja rute.

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

## Urmărirea poziției vehiculului

După migrarea bazei (`dotnet ef database update --project LogisticsRoutes.BusinessLayer --startup-project LogisticsRoutes.API`), API-ul acceptă `POST /api/routes/{routeId}/position` cu `latitude`, `longitude`, `reportedAt` (ISO 8601 cu fus orar) și `source` (`Reported` sau `Simulated`). Scrierea cere `Authorization: Bearer <token>`; tokenul este semnat pe server, limitat la ruta din URL și la una dintre sursele `Reported` sau `Simulated`, și expiră după 24 de ore. Fără token sau cu token invalid răspunde `401`, iar cu un token valid pentru altă rută ori sursă răspunde `403`. Fără cheia serverului, scrierea este dezactivată (`503`). `GET` pe aceeași adresă rămâne disponibil Dispecerului și întoarce ultima poziție, `204` dacă ruta există dar nu are raportări și `404` pentru ruta inexistentă. Coordonatele din afara intervalelor latitudine −90…90 și longitudine −180…180, ora lipsă sau cu peste 5 minute în viitor și sursa necunoscută primesc `400`. O raportare mai veche decât ultima salvată primește `409`. Sunt salvate separat momentul raportării și momentul primirii. Raportarea nu schimbă opririle sau comenzile.

Dispecerul citește poziția rutei selectate la fiecare 3 secunde, fără reîncărcarea paginii. Marcajul vehiculului este distinct de opriri. Interfața arată momentul ultimei primiri și marchează datele drept vechi când ora raportării este cu peste 60 de secunde în urmă; arată și absența sau indisponibilitatea poziției. O poziție cu `source: "Simulated"` este etichetată vizibil ca **simulată, nu GPS real**. Sursa `Reported` indică o raportare autorizată pentru acea rută; nu dovedește că dispozitivul sau coordonatele sunt autentice și nu oferă ETA.

Pentru ruta selectată, Dispecerul arată separat opririle încheiate din total și numărul celor `Delivered`, `Refused` și `PartialReturn`. Toate trei sunt stări finale; o oprire refuzată nu este numărată ca livrată. Numărul `Sequence` rămâne pe fiecare marcaj, iar culoarea arată starea din legenda hărții; popup-ul păstrează statusul scris. Comenzile și rutele zilei se reîmprospătează aproximativ la 30 de secunde, păstrând ruta selectată. Ora ultimei actualizări reușite este afișată în Dispecer; dacă o cerere eșuează, ultimele date rămân vizibile și apare un mesaj discret. Intervalul separat de 3 secunde pentru poziția vehiculului rămâne neschimbat.

### Capacitate și încheiere estimată în Dispecer

Pentru ruta selectată, bara de capacitate arată volumul total din API raportat la capacitatea vehiculului, procentul folosit și spațiul rămas. La o comandă `Confirmed`, selectorul arată rutele eligibile din aceeași zi și zonă, cu opriri încă neîncepute, inclusiv cele în care comanda nu încape. Sub selector apare volumul proiectat; dacă depășește capacitatea, butonul **Adaugă la rută** este dezactivat. Acesta este doar un calcul preventiv din datele afișate: API-ul verifică din nou în tranzacție și un conflict `409` apărut între timp este afișat cu mesajul său, apoi listele sunt reîncărcate. Volumele sunt afișate cu până la trei zecimale, ca în baza de date.

Pe desktop, poți trage o comandă `Confirmed` de mânerul **Trage pe o rută** și o poți lăsa pe cardul unei rute din ziua selectată. În timpul tragerii, rutele care o acceptă sunt evidențiate cu verde; cele incompatibile arată motivul (zonă, zi, opriri începute sau capacitate). Tragerea singură nu schimbă datele. La drop pe o rută compatibilă, Dispecerul folosește același `POST /api/routes/{routeId}/orders` ca butonul **Adaugă la rută** și așteaptă succesul API-ului înainte de reîncărcarea comenzilor și rutelor. Harta se actualizează, iar ruta selectată rămâne selectată. Un conflict `409` este afișat în română cu mesajul primit de la API. Pe mobil și cu tastatura, folosește selectorul de rută și butonul **Adaugă la rută**; acestea rămân disponibile indiferent de drag and drop.

Estimarea de încheiere folosește ultima poziție a vehiculului și cererea OSRM Route de la acea poziție prin **opririle care nu sunt încă finale**, în ordinea `Sequence`. Adaugă durata OSRM de condus la `numărul opririlor rămase × minutele de staționare per oprire`. Minutele de staționare sunt un parametru operațional, nu o măsurătoare din API. Configurează în `frontend/.env.local` înainte de pornirea Vite sau de build (model în `frontend/.env.example`):

```dotenv
VITE_OSRM_BASE_URL=http://127.0.0.1:5000
VITE_ETA_STOP_MINUTES=5
# Opțional, HH:mm în Europe/Chisinau pentru ziua livrării:
VITE_DELIVERY_CUTOFF_TIME=18:00
```

`VITE_ETA_STOP_MINUTES` este un număr întreg între 0 și 240; dacă lipsește, valoarea este **5 min/opire**. O valoare invalidă oprește calculul și este indicată în Dispecer. `VITE_DELIVERY_CUTOFF_TIME` este opțională și acceptă `HH:mm` în intervalul `00:00`–`23:59`. Dacă lipsește, nu apare avertizarea de termen; dacă este invalidă, estimarea devine indisponibilă până la corectarea configurației. Cu termen configurat, Dispecerul avertizează când încheierea estimată depășește ora-limită din **ziua livrării**, în fusul `Europe/Chisinau`. Setările `VITE_*` sunt incluse la build și necesită repornirea Vite sau reconstruirea frontendului după modificare.

Poziția trebuie să existe, să aibă coordonate valide și să fi fost raportată în ultimele **60 de secunde**. Dacă poziția lipsește/este veche, o oprire rămasă are coordonate invalide, OSRM nu răspunde/nu găsește traseu ori nu furnizează durată, Dispecerul afișează **„Estimare indisponibilă”** și motivul, fără o oră inventată. Cererea către OSRM are timeout de 8 secunde și se reîncearcă periodic; poziția se citește în continuare la 3 secunde. O poziție `Simulated` poate fi folosită pentru test, dar rămâne etichetată ca simulată. Estimarea folosește profilul auto OSRM și **nu include trafic în timp real**, pauze neprevăzute sau timpul până la depozit după ultima oprire. Nu completează `RouteStop.EstimatedArrival` și nu se salvează în bază. Serviciul OSRM trebuie să fie accesibil din browserul Dispecerului și să permită CORS.

Generează o cheie aleatoare pentru semnarea tokenurilor și păstreaz-o numai pe server. În dezvoltare, [User Secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets) o păstrează în afara repository-ului; repornește API-ul după configurare:

```powershell
$keyBytes = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($keyBytes)
$rng.Dispose()
$signingKey = [Convert]::ToBase64String($keyBytes)
dotnet user-secrets set 'DriverAccess:SigningKey' $signingKey --project LogisticsRoutes.API
```

În alte medii, setează `DriverAccess__SigningKey` în mediul procesului API la aceeași valoare Base64 (minimum 32 de octeți aleatori). O schimbare a cheii invalidează toate tokenurile emise anterior. Emite un cod separat pentru fiecare rută, dintr-un terminal local, cu API-ul deja compilat:

```powershell
dotnet run --no-build --project LogisticsRoutes.API --launch-profile http -- --issue-driver-token '<ID-ul-rutei>'
```

Codul afișat este valabil 24 de ore numai pentru poziții `Reported` pe acea rută. Transmite-l privat șoferului; URL-ul paginii conține numai ID-ul rutei, iar codul este introdus în pagină și păstrat doar în memoria ei. Pentru simulator emite un cod separat, limitat la `Simulated`:

```powershell
dotnet run --no-build --project LogisticsRoutes.API --launch-profile http -- --issue-simulator-token '<ID-ul-rutei>'
$env:SIMULATOR_ROUTE_TOKEN = '<codul de simulator afișat>'
```

Pentru a vedea marcajul mișcându-se pe străzi în dezvoltare, pornește API-ul, frontendul și OSRM local, apoi alege o rută de test cu minimum două opriri. Copiază ID-ul rutei din `GET http://localhost:5212/api/routes?day=YYYY-MM-DD` și rulează din rădăcina repository-ului:

```powershell
./scripts/dev/simulate-route-position.ps1 -RouteId '<ID-ul-rutei>' -IntervalSeconds 2
```

Simulatorul cere `SIMULATOR_ROUTE_TOKEN` (sau parametrul `-AccessToken`) și acceptă numai API și OSRM locale (`localhost` sau `127.0.0.1`). Cere de la OSRM geometria rutieră a opririlor în ordinea `Sequence`, exact ca harta, și trimite poziții `Simulated` la distanțe egale de-a lungul acesteia, la intervalul ales. Pentru închiderea ciclului, marcajul revine pe aceeași linie rutieră, în sens invers. Repetă ciclul până la `Ctrl+C`; `-Cycles 1` execută un singur ciclu. Pentru alte porturi locale folosește `-ApiBaseUrl 'http://127.0.0.1:<port>'` și/sau `-OsrmBaseUrl 'http://127.0.0.1:<port>'`. `-StepsPerLeg` stabilește numărul de raportări pentru fiecare oprire din ciclu. Dacă OSRM nu răspunde sau nu găsește traseu, simulatorul afișează motivul și trece explicit la **FALLBACK LINIE DREAPTĂ**, cu interpolarea inițială între opriri. Pozițiile rămân artificiale și nu reprezintă poziția reală a vehiculului. Simulatorul nu trimite coordonatele comenzilor către un serviciu public.

### Test pe telefon, prin HTTPS local

Pagina șoferului este la `/driver/<ID-ul-rutei>`. Pentru un test pe telefon, ține API-ul pornit pe calculator la `localhost:5212`, pe aceeași rețea privată cu telefonul, și construiește frontendul cu `npm.cmd --prefix frontend run build`. [Geolocation cere un context HTTPS de încredere](https://developer.mozilla.org/en-US/docs/Web/API/Geolocation/watchPosition); accesarea adresei HTTP cu IP-ul calculatorului nu este suficientă. [Instalează Caddy](https://caddyserver.com/docs/install) și folosește [certificatul său local](https://caddyserver.com/docs/automatic-https#local-https) pentru pagină și pentru numai cele două cereri API necesare șoferului:

```caddyfile
https://192.168.1.50 {
    tls internal
    root * "C:/cale/catre/LogisticsRoutes/frontend/dist"

    @routeRead {
        method GET
        path /api/routes/ROUTE_ID
    }
    @positionWrite {
        method POST
        path /api/routes/ROUTE_ID/position
    }
    @otherApi path /api/*

    route {
        reverse_proxy @routeRead 127.0.0.1:5212
        reverse_proxy @positionWrite 127.0.0.1:5212
        respond @otherApi 404
        try_files {path} /index.html
        file_server
    }
}
```

Înlocuiește IP-ul, `ROUTE_ID` și calea absolută către `frontend/dist`, salvează blocul într-un `Caddyfile` local și rulează `caddy run --config Caddyfile`. Configurația permite din LAN doar citirea rutei alese și raportarea poziției pentru ea; API-ul Dispecerului rămâne la `localhost`. Pe Windows, certificatul CA de instalat pe telefon este de obicei la `$env:APPDATA\Caddy\pki\authorities\local\root.crt`; [Caddy documentează directorul de date](https://caddyserver.com/docs/conventions#data-directory) și [cerința de încredere pentru alte dispozitive](https://caddyserver.com/docs/running). Deschide `https://<IP-LAN>/driver/<ID-ul-rutei>` numai după ce browserul acceptă certificatul fără avertisment. Permite în firewall accesul la portul 443 doar din rețeaua privată și nu configura redirecționare de porturi sau tunel public. Caddy este singura intrare din LAN.

Lipește codul `--issue-driver-token` în pagină și apasă **Pornește partajarea poziției**. Abia atunci browserul cere permisiunea; pagina arată starea ei, semnalul GPS și ora ultimei raportări. Trimite cel mult o raportare la 3 secunde. **Oprește** dezactivează urmărirea și oprește cererile noi; o cerere deja primită de server poate rămâne salvată. Dacă permisiunea este refuzată, activeaz-o din setările browserului; dacă lipsește semnalul, pagina rămâne în așteptare și indică problema. Ține pagina deschisă: această primă versiune nu raportează în fundal după închiderea sau suspendarea ei. Verifică în Dispecer apariția poziției `Reported` pentru aceeași rută.

## Planificarea rutelor

Înainte de `POST /api/routes/plan`, configurează coordonatele **depozitului tău** prin `Planning:DepotLatitude` și `Planning:DepotLongitude`. Nu există coordonate implicite.

Latitudinea trebuie să fie între −90 și 90, iar longitudinea între −180 și 180. Poți seta aceleași chei și în configurația locală ASP.NET Core, sub secțiunea `Planning`.

Cererea conține ziua de planificare: `{"day":"YYYY-MM-DD"}`. Serviciul include doar comenzile `Confirmed` din acea zi, păstrează fiecare rută într-o singură zonă, respectă capacitatea vehiculului și atribuie un vehicul și un șofer distinct fiecărei rute. Opririle sunt ordonate prin nearest neighbor cu distanța Haversine de la depozit. Ora estimată a sosirii rămâne necompletată până când există date pentru o estimare reală. Dacă nu sunt comenzi confirmate, răspunsul conține `routes: []`; dacă planificarea nu poate fi completată sau există deja rute în acea zi, API-ul răspunde cu `409` fără să salveze o planificare parțială.

## Starea unei opriri

`PATCH /api/routes/{routeId}/stops/{stopId}/status` primește un corp JSON precum `{"status":"Departed"}`. Stările permise urmează `Pending → Departed → Arrived → Delivered / Refused / PartialReturn`; ultimele trei sunt finale în acest MVP. API-ul întoarce `400` pentru un nume de status necunoscut, `404` dacă ruta sau oprirea nu există ori oprirea nu aparține rutei, și `409` pentru o tranziție nepermisă. La `Delivered`, comanda asociată devine tot `Delivered`; la `Refused` sau `PartialReturn`, comanda rămâne `Planned` până la definirea unui flux ulterior.
