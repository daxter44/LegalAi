-- US-2.12: analityka Umami — OSOBNA baza + rola bez żadnego dostępu do bazy z rozmowami
-- (docs/PLAN-US-2.12-UMAMI.md, U-2). Ten plik wykonuje się WYŁĄCZNIE przy inicjalizacji
-- ŚWIEŻEGO wolumenu Postgresa (docker-entrypoint-initdb.d); na istniejącym klastrze
-- (M4, dev z danymi) odpowiednik uruchamia się ręcznie — docs/RUNBOOK-UMAMI.md.
--
-- Hasło 'umami' jest DEV-ONLY (kontener bez publicznego portu bazy); na produkcji silny sekret.
CREATE ROLE umami LOGIN PASSWORD 'umami';
CREATE DATABASE umami OWNER umami;

-- Twarda ściana w obie strony: domyślny grant CONNECT dla PUBLIC to furtka — zdejmujemy go,
-- żeby rola umami nie mogła nawet otworzyć połączenia z bazą praworag (i odwrotnie).
REVOKE CONNECT ON DATABASE praworag FROM PUBLIC;
GRANT  CONNECT ON DATABASE praworag TO praworag;
REVOKE CONNECT ON DATABASE umami    FROM PUBLIC;
GRANT  CONNECT ON DATABASE umami    TO umami;
