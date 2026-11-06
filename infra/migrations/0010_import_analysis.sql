-- E03.2: resultado da analise e pacote cifrado proprio. analysis guarda so estrutura: nunca valores de celula.
ALTER TABLE hub.import_files
  ADD COLUMN source_format text,
  ADD COLUMN firmware_line text NOT NULL DEFAULT 'undetermined' CHECK (firmware_line IN ('undetermined', 'v5', 'v6')),
  ADD COLUMN parser_status text,
  ADD COLUMN analysis jsonb,
  ADD COLUMN unlock_attempts integer NOT NULL DEFAULT 0,
  ADD COLUMN unlocked_from_package boolean NOT NULL DEFAULT false,
  ADD COLUMN content_sha256 bytea;

ALTER TABLE hub.import_files DROP CONSTRAINT import_files_status_chk;
ALTER TABLE hub.import_files ADD CONSTRAINT import_files_status_chk
  CHECK (status IN ('created', 'uploading', 'uploaded', 'quarantined', 'inspecting', 'awaiting_password', 'inspected', 'rejected', 'cancelled'));
