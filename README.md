# LogisticsRoutes

Pentru PostgreSQL local:

1. Copiază exemplul: `Copy-Item .env.example .env`.
2. Completează `POSTGRES_PASSWORD` în `.env`; poți schimba și numele bazei sau utilizatorul. Fișierul `.env` este ignorat de Git.
3. Pornește baza: `docker compose up -d`. Verifică starea cu `docker compose ps`.
4. Oprește baza: `docker compose down`. Volumul numit păstrează datele între porniri.

API-ul rulează separat. Pentru conectare, setează variabila de mediu `ConnectionStrings__LogisticsDb` la un șir de forma `Host=127.0.0.1;Port=5433;Database=<POSTGRES_DB>;Username=<POSTGRES_USER>;Password=<POSTGRES_PASSWORD>`, folosind valorile din `.env`.

## Planificarea rutelor

Înainte de `POST /api/routes/plan`, configurează coordonatele **depozitului tău** prin `Planning:DepotLatitude` și `Planning:DepotLongitude`. Nu există coordonate implicite. Pentru variabile de mediu, înlocuiește valorile dintre paranteze cu coordonatele depozitului:

```powershell
$env:Planning__DepotLatitude = '<latitudine_depozit>'
$env:Planning__DepotLongitude = '<longitudine_depozit>'
```

Latitudinea trebuie să fie între −90 și 90, iar longitudinea între −180 și 180. Poți seta aceleași chei și în configurația locală ASP.NET Core, sub secțiunea `Planning`.

Cererea conține ziua de planificare: `{"day":"YYYY-MM-DD"}`. Serviciul include doar comenzile `Confirmed` din acea zi, păstrează fiecare rută într-o singură zonă, respectă capacitatea vehiculului și atribuie un vehicul și un șofer distinct fiecărei rute. Opririle sunt ordonate prin nearest neighbor cu distanța Haversine de la depozit. Ora estimată a sosirii rămâne necompletată până când există date pentru o estimare reală. Dacă nu sunt comenzi confirmate, răspunsul conține `routes: []`; dacă planificarea nu poate fi completată sau există deja rute în acea zi, API-ul răspunde cu `409` fără să salveze o planificare parțială.
