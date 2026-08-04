CREATE TABLE IF NOT EXISTS schema_migrations (
  version TEXT PRIMARY KEY,
  applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS tracks (
  id              BIGSERIAL PRIMARY KEY,
  source          TEXT NOT NULL CHECK (source IN ('youtube','url','soundcloud')),
  external_id     TEXT NOT NULL,
  title           TEXT NOT NULL,
  webpage_url     TEXT,
  thumbnail_url   TEXT,
  duration_seconds DOUBLE PRECISION,
  playable_url    TEXT,
  source_bytes    BIGINT,
  is_too_large    BOOLEAN NOT NULL DEFAULT FALSE,
  created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
  last_played_at  TIMESTAMPTZ,
  UNIQUE (source, external_id)
);

CREATE INDEX IF NOT EXISTS ix_tracks_last_played ON tracks (last_played_at DESC NULLS LAST);

CREATE TABLE IF NOT EXISTS track_aliases (
  alias_key   TEXT PRIMARY KEY,
  track_id    BIGINT NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
  created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS clan_settings (
  clan_id                   BIGINT PRIMARY KEY,
  owner_id                  BIGINT,
  dj_role_id                BIGINT,
  default_stream_channel_id BIGINT,
  vote_skip_enabled         BOOLEAN NOT NULL DEFAULT FALSE,
  vote_skip_ratio           REAL NOT NULL DEFAULT 0.5
                            CHECK (vote_skip_ratio > 0 AND vote_skip_ratio <= 1),
  updated_at                TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS clan_command_channels (
  clan_id    BIGINT NOT NULL REFERENCES clan_settings(clan_id) ON DELETE CASCADE,
  channel_id BIGINT NOT NULL,
  added_by   BIGINT,
  added_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (clan_id, channel_id)
);

CREATE TABLE IF NOT EXISTS play_history (
  id                   BIGSERIAL PRIMARY KEY,
  clan_id              BIGINT NOT NULL,
  track_id             BIGINT NOT NULL REFERENCES tracks(id),
  mode                 TEXT NOT NULL CHECK (mode IN ('voice','streaming')),
  channel_id           BIGINT NOT NULL,
  requested_by_user_id BIGINT,
  started_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
  ended_at             TIMESTAMPTZ,
  end_reason           TEXT CHECK (end_reason IN (
                         'completed','skip','vote_skip','stop','error','too_large','restart'
                       ))
);

CREATE INDEX IF NOT EXISTS ix_history_clan_started ON play_history (clan_id, started_at DESC);
CREATE INDEX IF NOT EXISTS ix_history_track ON play_history (track_id);

CREATE TABLE IF NOT EXISTS playlists (
  id           BIGSERIAL PRIMARY KEY,
  clan_id      BIGINT NOT NULL,
  name         TEXT NOT NULL,
  created_by   BIGINT,
  created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_playlists_clan_lower_name
  ON playlists (clan_id, lower(name));

CREATE TABLE IF NOT EXISTS playlist_items (
  id          BIGSERIAL PRIMARY KEY,
  playlist_id BIGINT NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
  position    INT NOT NULL,
  track_id    BIGINT NOT NULL REFERENCES tracks(id),
  added_by    BIGINT,
  added_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE (playlist_id, position)
);

CREATE INDEX IF NOT EXISTS ix_playlist_items_track ON playlist_items (track_id);
