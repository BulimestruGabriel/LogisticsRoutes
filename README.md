# LogisticsRoutes

Pentru PostgreSQL local:

1. Copiază exemplul: `Copy-Item .env.example .env`.
2. Completează `POSTGRES_PASSWORD` în `.env`; poți schimba și numele bazei sau utilizatorul. Fișierul `.env` este ignorat de Git.
3. Pornește baza: `docker compose up -d`. Verifică starea cu `docker compose ps`.
4. Oprește baza: `docker compose down`. Volumul numit păstrează datele între porniri.

API-ul rulează separat. Pentru conectare, setează variabila de mediu `ConnectionStrings__LogisticsDb` la un șir de forma `Host=127.0.0.1;Port=5433;Database=<POSTGRES_DB>;Username=<POSTGRES_USER>;Password=<POSTGRES_PASSWORD>`, folosind valorile din `.env`.
