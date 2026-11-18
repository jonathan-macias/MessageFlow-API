-- Se ejecuta UNA sola vez, cuando el volumen pgdata está vacío.
-- La migración AddDatasetSemanticSearch también hace CREATE EXTENSION IF NOT EXISTS,
-- esto solo garantiza que exista desde el primer arranque.
CREATE EXTENSION IF NOT EXISTS vector;

-- Base de datos propia para Evolution API (mismo servidor Postgres).
CREATE DATABASE evolution;
