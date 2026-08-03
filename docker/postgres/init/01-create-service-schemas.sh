#!/bin/sh
set -eu

psql --set ON_ERROR_STOP=1 \
  --username "$POSTGRES_USER" \
  --dbname "$POSTGRES_DB" \
  --set identity_user="$IDENTITY_DB_USER" \
  --set identity_password="$IDENTITY_DB_PASSWORD" \
  --set controlplane_user="$CONTROLPLANE_DB_USER" \
  --set controlplane_password="$CONTROLPLANE_DB_PASSWORD" \
  --set orchestrator_user="$ORCHESTRATOR_DB_USER" \
  --set orchestrator_password="$ORCHESTRATOR_DB_PASSWORD" \
  --set integrations_user="$INTEGRATIONS_DB_USER" \
  --set integrations_password="$INTEGRATIONS_DB_PASSWORD" <<-'EOSQL'
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', :'identity_user', :'identity_password') \gexec
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', :'controlplane_user', :'controlplane_password') \gexec
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', :'orchestrator_user', :'orchestrator_password') \gexec
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', :'integrations_user', :'integrations_password') \gexec

REVOKE CREATE ON SCHEMA public FROM PUBLIC;

CREATE SCHEMA identity AUTHORIZATION :"identity_user";
CREATE SCHEMA control_plane AUTHORIZATION :"controlplane_user";
CREATE SCHEMA orchestrator AUTHORIZATION :"orchestrator_user";
CREATE SCHEMA integrations AUTHORIZATION :"integrations_user";

ALTER ROLE :"identity_user" SET search_path TO identity;
ALTER ROLE :"controlplane_user" SET search_path TO control_plane;
ALTER ROLE :"orchestrator_user" SET search_path TO orchestrator;
ALTER ROLE :"integrations_user" SET search_path TO integrations;
EOSQL
