# Breaking changes

| Change | Notes |
|--------|-------|
| `{state}` name-format / on-complete variable for songs now uses split lifecycle/activity/outcome wording | Runtime state is split into lifecycle, activity phase, terminal outcome, and skip reason |
| String/list string input defaults to album mode; `-s` / `--song` added; source upgrades split into `--upgrade-to-album` | Add `song = true` to config to restore the old string/list default. Use `--upgrade-to-album` / `upgrade-to-album = true` to convert song-shaped source results such as CSV rows or Spotify playlist tracks into album jobs. API setting patches now use `requestedMode` and `upgradeToAlbum` instead of `isAlbum`. |
| `--parallel-album-search` / `parallelAlbumSearch` removed | Superseded by full parallel search+download |
| `--concurrent-processes` / `--concurrent-downloads` removed | Downloads are now unlimited; `--concurrent-searches` replaces the search-limiting role |
