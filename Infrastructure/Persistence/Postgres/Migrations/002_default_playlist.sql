ALTER TABLE playlists
  ADD COLUMN IF NOT EXISTS is_default BOOLEAN NOT NULL DEFAULT FALSE;

CREATE UNIQUE INDEX IF NOT EXISTS ux_playlists_clan_default
  ON playlists (clan_id)
  WHERE is_default;
