ALTER TABLE tracks
  ADD COLUMN IF NOT EXISTS playable_video_url TEXT;
