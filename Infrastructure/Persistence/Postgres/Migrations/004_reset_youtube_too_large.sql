UPDATE tracks
SET is_too_large = FALSE,
    source_bytes = NULL,
    updated_at = now()
WHERE source = 'youtube'
  AND is_too_large;
