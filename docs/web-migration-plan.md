# Web/Docker migration plan

## Target stack
- `worker` (.NET Worker Service): CSV import, scheduled refreshes, download queue, targeted API refreshes.
- `web` (ASP.NET Core Razor + Vite/SCSS/TypeScript): e621 UI clone incl. Advanced Options search panel.
- Volumes: `/data/db`, `/data/media` (md5 two-level layout), `/data/imports`.
- Optional reverse proxy (Caddy/Traefik) for TLS/auth.

## Steps
1. Add `Worker`; build import/download logic on `Core`/`Data`.
2. Add `Web` scaffold (Vite, SCSS, TS), port base layout and theme styles from e621ng; record the upstream commit here.
3. Posts index + Advanced Options search against `Data`.
4. Post show, pools, tags; queue/watch/settings/import-history pages.
5. Implement search syntax (metatags: order, rating, score, wildcards, -tag, ~tag).
6. Junction tables (`PostTags`, `PoolPosts`) in the importer.
7. Docker/compose.

## Open
- Auth (LAN-only vs login).
- Which e621ng pages to stub/hide (comments, favorites, notes, votes, accounts).
- Pool and artist-to-character discovery, inspired by the old Python tool.
