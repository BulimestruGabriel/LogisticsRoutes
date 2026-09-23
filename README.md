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

## Planificarea rutelor

Înainte de `POST /api/routes/plan`, configurează coordonatele **depozitului tău** prin `Planning:DepotLatitude` și `Planning:DepotLongitude`. Nu există coordonate implicite.

Latitudinea trebuie să fie între −90 și 90, iar longitudinea între −180 și 180. Poți seta aceleași chei și în configurația locală ASP.NET Core, sub secțiunea `Planning`.

Cererea conține ziua de planificare: `{"day":"YYYY-MM-DD"}`. Serviciul include doar comenzile `Confirmed` din acea zi, păstrează fiecare rută într-o singură zonă, respectă capacitatea vehiculului și atribuie un vehicul și un șofer distinct fiecărei rute. Opririle sunt ordonate prin nearest neighbor cu distanța Haversine de la depozit. Ora estimată a sosirii rămâne necompletată până când există date pentru o estimare reală. Dacă nu sunt comenzi confirmate, răspunsul conține `routes: []`; dacă planificarea nu poate fi completată sau există deja rute în acea zi, API-ul răspunde cu `409` fără să salveze o planificare parțială.

## Starea unei opriri

`PATCH /api/routes/{routeId}/stops/{stopId}/status` primește un corp JSON precum `{"status":"Departed"}`. Stările permise urmează `Pending → Departed → Arrived → Delivered / Refused / PartialReturn`; ultimele trei sunt finale în acest MVP. API-ul întoarce `400` pentru un nume de status necunoscut, `404` dacă ruta sau oprirea nu există ori oprirea nu aparține rutei, și `409` pentru o tranziție nepermisă. La `Delivered`, comanda asociată devine tot `Delivered`; la `Refused` sau `PartialReturn`, comanda rămâne `Planned` până la definirea unui flux ulterior.
