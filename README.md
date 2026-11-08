# SpankBank

A self-hosted **e621 catalog browser and download manager**. It keeps a local copy of e621's public database exports,
lets you pick the artists, pools and searches you care about, downloads the matching files, and gives you a web
interface that is a faithful clone of e621's own site (same markup, same styles, same search syntax) to browse the
result, entirely offline from then on.

It runs as a small Docker stack on a home server or any always-on machine:

| Container | What it does |
|---|---|
| `worker` | Downloads and imports e621's daily database exports, matches your watches against the catalog, downloads the files (with a recovery path for posts e621 has deleted), makes thumbnails for Flash files, empties the rubbish bin. |
| `web` | The site you browse: local posts, favorites, artists, pools, tags, downloads, settings. |

> **Heads-up.** e621 is an adult site and SpankBank archives what you tell it to, including explicit material. It is for
> personal, private use by adults. It has **no login** (see [Access and security](#access-and-security)), is not
> affiliated with e621, and is not a substitute for supporting the site.

**Contents:**
[Features](#features) ·
[How it works](#how-it-works) ·
[Requirements](#requirements) ·
[Quick start](#quick-start) ·
[Configuration](#configuration) ·
[First steps in the web interface](#first-steps-in-the-web-interface) ·
[Using SpankBank](#using-spankbank) ·
[Access and security](#access-and-security) ·
[Operating it](#operating-it-updates-backups-moving-data) ·
[Archiving another e621ng site](#archiving-another-e621ng-site) ·
[Building and developing without Docker](#building-and-developing-without-docker) ·
[Troubleshooting](#troubleshooting) ·
[Project layout](#project-layout) ·
[Credits and licence](#credits-and-licence)

---

## Features

- **Complete local catalog.** Imports e621's database exports (posts, tags, tag aliases, pools) into
  SQLite, refreshed every day. Millions of posts are searchable without touching the site.
- **Watches decide what is downloaded.** Watch an *artist* (with their aliases and characters found automatically),
  a *pool*, or any *search* in e621 syntax. Only posts matched by an enabled watch are downloaded.
- **e621 look and search.** Posts, pools, artists and tags pages reproduce e621ng's markup and styles, so it feels like
  the real thing: tag sidebar, advanced search panel, `order:`, `rating:`, `score:`, `-tag`, `~tag`, `( ~a ~b )`
  groups, `*` wildcards, pool and parent/child searches and more.
- **Local and online side by side.** Every tag shows how many posts you have locally next to the count on the site.
  Artist and pool lists have a *Local* (your watches) and an *Online* (the whole site) view.
- **Favorites with nested folders**, a star on every post, and a favorites listing you can search.
- **Blacklist** in e621 syntax: matching posts are never downloaded and are hidden in your listings.
- **Delete mode and a rubbish bin.** Deleted posts are kept for 30 days and are never downloaded again. *Stray* posts
  (downloaded, but no longer matched by any watch) can be found and binned in one go.
- **Getting deleted posts back.** For posts e621 has deleted, the worker tries the sources listed on the post (direct
  image hosts, Inkbunny) and the Wayback Machine, and only accepts a file whose MD5 matches the catalog's.
- **Flash.** `.swf` posts play in the browser with the [Ruffle](https://ruffle.rs) emulator, and the worker renders
  thumbnails for them (e621 has none).
- **Polite by design.** The catalog comes from the bulk exports. The API and the file servers are only used for the
  files you asked for, with a proper User-Agent, bounded parallelism, a daily limit and back-off on errors.
- **Any e621ng site.** Point it at another site that runs e621ng (for example e6ai) with three environment variables.

## How it works

```
                 e621 database exports (daily, CSV)             e621 file servers / Wayback / Inkbunny
                               │                                              │
                               ▼                                              ▼
 browser  ◄──►  web  ──wake──► worker  ── import ──►  server.sqlite     media/<md5[0:2]>/<md5[2:4]>/<md5>.<ext>
                 │              │                       (the catalog)          (the downloaded files)
                 │              └── downloads, bin ──►  device.sqlite  (what is saved locally)
                 └── watches, favorites, settings ──►  SpankBank.sqlite (your state)
```

- **Three SQLite databases** (WAL mode) in `data/db`:
  - `server.sqlite`: the raw imported catalog, rebuilt from the exports. Big (about 9 GB with all of e621) but
    disposable: the next import recreates it.
  - `device.sqlite`: what is downloaded or restored on this device.
  - `SpankBank.sqlite`: your state: watches, favorites, blacklist, settings, queue, import history.
- **Imported is not downloaded.** The catalog knows every post on the site; only watched posts become local files.
- **One writer per job.** The worker owns imports and downloads. The web app only writes small things (watches,
  favorites, settings, queue rows) and then wakes the worker over the compose network (`http://worker:8081`,
  not published). There is no polling.
- **Media layout** is e621's: `media/ab/cd/abcd….jpg`, with `preview/` and `sample/` folders beside it. There are no
  per-artist or per-tag folders; the database is the index. Nothing under `media/.bin/` (the rubbish bin) is served.

## Requirements

- A machine that is on most of the time, with **Docker Engine and the Docker Compose plugin v2.24 or newer** (the compose file uses the long `env_file`
  syntax). Linux is what it is developed and run on; other platforms are untested (mind that bind mounts can be slow
  for the databases on Docker Desktop).
- **Disk space.** About 10 GB for the databases and imports, plus room for what you download (e621 files are
  commonly 0.1–10 MB each; a few hundred thousand posts is hundreds of GB).
- **Fast storage for the databases.** Keep `data/db` on an SSD/NVMe. SQLite does many small random reads, and on a
  spinning disk or a network share pages such as *Status* become painfully slow. The media folder can live on a big,
  slow disk.
- **Network access** to e621 (the exports, the file servers). Optionally the Wayback Machine and Inkbunny.
- To build the images: the first build downloads the .NET and Node images and packages, and the one-time Flash
  helper image compiles Rust (**about 30 minutes**, several GB of build cache).
- No GPU needed. Flash previews render on software Vulkan inside the worker image.

## Quick start

```bash
# 1. Get the code
git clone https://github.com/DoubleThePsycho/SpankBank.git
cd SpankBank

# 2. Your configuration
cp compose.example.yaml compose.yaml
cp example.env .env              # then edit it: at least have a look at the paths and the port (see "Configuration")

# 3. Folders for the data (must be writable by SPANKBANK_UID:SPANKBANK_GID, 1000:1000 by default)
mkdir -p data/db data/imports data/keys data/media
#    If your user is not 1000: set SPANKBANK_UID / SPANKBANK_GID in .env, or run: sudo chown -R 1000:1000 data

# 4. Build the Flash helper image once (about 30 minutes; the worker image copies one binary out of it)
docker compose --profile tools build ruffle-exporter
#    (equivalent: docker build -f docker/ruffle-exporter.Dockerfile -t spankbank-ruffle-exporter:0.7.1 .)

# 5. Build the two application images (a few minutes the first time)
docker compose build

# 6. Start
docker compose up -d
docker compose ps
```

Open **http://127.0.0.1:8621** (or the address and port you set in `.env`). The site is empty at first: continue with
[First steps](#first-steps-in-the-web-interface).

Things worth knowing right away:

- The images are **built locally** from this checkout; there is nothing to pull from a registry.
- Docker's image clean-up (`docker image prune -a`, or a tool that does it on a schedule) removes the Flash helper
  image `spankbank-ruffle-exporter:0.7.1` because no container runs it. The *next worker build then fails*. Rebuild it
  with step 4 (fast while the build cache is there), or keep a copy: `docker save spankbank-ruffle-exporter:0.7.1 | gzip > exporter.tar.gz`
  and `docker load -i exporter.tar.gz`.
- Use a different directory name or `-p name` per instance if you run more than one stack on a host (see
  [Archiving another e621ng site](#archiving-another-e621ng-site)).

## Configuration

All settings are environment variables in `.env` (next to `compose.yaml`; every line is optional). The compose file
reads it for the `${...}` values, and the worker also receives it as its environment.

| Variable | Default | Meaning |
|---|---|---|
| `SITE_HOST` | `e621.net` | The e621ng site to archive. Read by both containers. |
| `STATIC_HOST` | `static1.<SITE_HOST>` | The site's file server. |
| `SITE_NAME` | first part of `SITE_HOST` | Short name used in page titles and texts; also picks the look (`e621`, `e6ai`; anything else gets e621's). |
| `INKBUNNY_USERNAME` / `INKBUNNY_PASSWORD` | empty (guest) | Optional [Inkbunny](https://inkbunny.net) account used to recover deleted posts whose source is an Inkbunny submission. The guest login only sees general-rated art; for mature art use a dedicated account with mature ratings (and API access) enabled. Quote passwords containing `$`: `INKBUNNY_PASSWORD='p@ss$word'`. Worker only. |
| `SPANKBANK_DB_DIR` | `./data/db` | Host folder for the three SQLite databases. **Fast storage.** |
| `SPANKBANK_IMPORTS_DIR` | `./data/imports` | Downloaded export files (a few recent ones are kept). |
| `SPANKBANK_KEYS_DIR` | `./data/keys` | The web app's key ring (antiforgery tokens). Must survive redeploys. |
| `SPANKBANK_MEDIA_DIR` | `./data/media` | The downloaded files. Can be a big, slow disk. |
| `SPANKBANK_UID` / `SPANKBANK_GID` | `1000` / `1000` | The user the containers run as; it must own the four folders. |
| `SPANKBANK_WEB_BIND` | `127.0.0.1` | The address the web port is published on. See [Access and security](#access-and-security). |
| `SPANKBANK_WEB_PORT` | `8621` | The published port. |

Everything else (daily update time, download limits and parallelism, list sizes, delete mode, pool auto-adding) is
set on the **Settings** page of the site and stored in the database. The defaults:

| Setting | Default |
|---|---|
| Update from the site every day at (UTC) | `04:00` |
| Download the posts your watches match | on |
| Most files per day (0 = no limit) | 2000 |
| Parallel downloads (1–64) | 3 |
| Rows per page in the artist and pool lists | 100 |
| Allow delete mode | off |
| Add an artist's pools automatically when at least this share of their posts is the artist's | on, 90 % |

Run `docker compose config` to see the final, merged configuration.

## First steps in the web interface

1. **Import the catalog.** Open **Status** and press **Update from e621 now**. The worker downloads the latest
   exports (they are large; progress is live on the page), imports them, and then re-matches your watches. This takes a
   while the first time. From then on it repeats by itself every day at the time set in Settings. Until it has
   finished, *Tags*, *Artists (Online)* and *Pools (Online)* are empty. Nothing is downloaded yet.
2. **Pick what to keep.** Open **Artists → Local** and press **Add artists**.
   - One name adds the artist **disabled** and opens their page, so you can look at the characters and filters before
     anything is fetched; press **Enable** when you are happy.
   - Several names (one per line) are added **enabled** at once, with a report of the ones that are not artist tags
     and suggestions for the misspelled ones.
   - You can also find artists in **Artists → Online** (the whole site) and press *Watch this artist* on their page.
3. **Let it download.** Matched posts are queued and fetched in the background; follow it on
   **Posts → Downloads** (progress, speed, ETA, and the posts that could not be saved). With the default limit of 2000
   files per UTC day, a big new watch is spread over several days; set the limit to `0` in Settings to download as fast
   as it can.
4. **Browse.** **Posts** lists what you have locally. Everything else (favorites, tags, pools) builds on that.

## Using SpankBank

### Posts and search

**Posts → Listing** shows the posts on this device with e621's search box, tag sidebar and advanced options.
The search syntax is e621's:

- tags separated by spaces, `-tag` to exclude, `~tag` for "any of", `( ~a ~b )` OR groups, `*` wildcards;
- metatags such as `rating:s`, `score:>50`, `favcount:`, `id:`, `width:`, `height:`, `filesize:`, `tagcount:`,
  `date:`, `age:`, `type:`/`filetype:`, `md5:`, `order:score`, `order:random`, `pool:<id>`, `parent:<id>`, …
- SpankBank's own terms: `fav:true` (favorites), `folder:<id|none|any>` (favorite folders), `stray:true|false`
  (downloaded but matched by no enabled watch and not a favorite) and `blacklisted:true|any|false`.

The **Mode** menu above the posts switches between *View*, *Favorite*, *Unfavorite* and (if enabled) *Delete*, so a
click on a post does something else. On a post page the sidebar shows the tags in categories, the file details
(rating, score, status, whether the file is the original, a recovered copy or just the sample), pool navigation bars
and parent/child relations.

The **tag sidebar** and the autocomplete show two counts per tag: **local** (posts on this device) and **online**
(posts on the site, as of the last export). Hover a count for the exact number.

### Artists

**Artists → Local** lists your watched artists with the number of posts in the exports, downloaded, restored,
lost and pending, and the characters found for them; sort by clicking a header, filter with the *Search* button.
An artist's own page (`/artist/<name>`) is where everything about them is managed:

- *Enable/Disable*, *Rescan*, *ignore the global blacklist*, *Remove* (it asks what to do with the posts you already
  downloaded: all of them, only those no other watch matches, or none; favorites are always kept);
- aliases, and **characters** as toggle chips (found automatically; add your own, enable or disable them all);
- the artist's **own filters** (whitelist and blacklist, one tag expression per line): a post must match at least one
  whitelist line (if there are any) and none of the blacklist lines to count as theirs;
- their pools and their local posts.

### Pools

**Pools → Local** lists watched pools (disabled ones are hidden unless you ask); **Pools → Online** searches every pool
on the site and offers *Watch*. Both have a gallery view. When a watched artist's posts make up most of a pool the pool
is added automatically (the threshold is in Settings); smaller shares appear as *suggestions* on the Local page.
A pool's page shows its posts in order and has a **Manage** button for its local name and settings.

### Tags and watched searches

**Tags** searches the catalog's tags with local and online counts. **Tags → Searches** holds watched *searches*: any
e621 search expression, for example `canine rating:s -comic`. Every post matching it is downloaded, exactly like the
posts of a watched artist.

### Favorites

Press the star on a post, or use *Favorite* mode on the listing. **Posts → Favorites** lists them newest-favorited first
and is searchable like the normal listing. Favorites sit in **nested folders**: create, rename and delete folders,
open them, and drag posts or folders onto other folders. Favorited posts are never treated as strays and never moved to
the bin automatically.

### Blacklist

**Settings → Edit the blacklist**: one e621-style search per line (tags, `-tag`, `~tag`, OR groups, `*`, `rating:`,
`score:`, …; no pool/parent/fav terms). A post matching any line is never downloaded and is hidden in listings; use
`blacklisted:true` or `blacklisted:any` in a search to see them anyway. A single artist can ignore the global blacklist
on their page.

### Delete mode and the rubbish bin

Off by default (**Settings → Allow delete mode**). When on, the Mode menu and the post toolbar get a *Delete* action.
The post is hidden at once; the worker moves its files into `media/.bin/` and, after 30 days (or *Empty bin*), deletes
them for good. A deleted post is **never downloaded again**. **Posts → Bin** lists, restores and empties. The bin also
has *Move all strays to the bin* for posts that no enabled watch matches any more (they are never moved automatically).

### Getting deleted posts back

e621 removes posts. For a post the site no longer serves, the worker tries, in order: the sources listed on the post
(a built-in allow-list of direct image hosts, and Inkbunny submissions through its API), then the Wayback Machine.
A file is accepted only if its MD5 equals the catalog's (the Wayback *sample* is the one exception and is marked as
"sample only"). Posts it cannot find anywhere are shown as *lost*. The Wayback Machine is asked at most once every
five seconds and backs off for up to an hour when it refuses; while it is backed off, deleted posts are left alone and
normal posts keep downloading.

### Flash

`.swf` posts play with Ruffle, which is bundled and served by the site itself (no external requests); links inside a
movie are blocked. The worker renders a thumbnail for each local `.swf` about every ten minutes using Ruffle's
headless exporter. Files it cannot render get a `.nopreview` marker beside the preview (delete it to retry).

### Settings

| Page | What it is for |
|---|---|
| **Status** | Last update, posts in the catalog, strays, import history, the *Update now* button, and live progress. |
| **Posts → Downloads** | The download queue: progress, speed, ETA and the posts that failed. |
| **Settings** | Daily update time, download switch / daily limit / parallelism, list size, delete mode, pool auto-adding. |
| **Settings → Blacklist** | The global blacklist. |

## Access and security

SpankBank is built for **one user on a private network**. It has **no accounts and no login**: anyone who can reach
the web port can browse everything, change your watches and delete your posts.

- By default the port is published on `127.0.0.1` only. To use it from your phone or another PC, set
  `SPANKBANK_WEB_BIND` to the address of a **private** interface (your LAN address, or your VPN address such as a
  Tailscale/WireGuard one) and keep the machine off the public internet.
- To expose it beyond that, put a reverse proxy **with authentication** in front (Caddy, Traefik, nginx + an auth
  proxy, …). In that case remove the `ports:` section of the `web` service and let the proxy join the compose network
  and reach `web:8080`.
- The worker's command port (8081) is only on the compose network and is never published.
- Credentials (`INKBUNNY_PASSWORD`) live in `.env`; keep that file private and out of version control (it is
  git-ignored).

## Operating it: updates, backups, moving data

**Update to a newer version**

```bash
git pull
docker compose build
docker compose up -d
```

Database changes are applied automatically at start (new tables and indexes are created when missing). Rebuild the
Flash helper image only if `docker/ruffle-exporter.Dockerfile` changed.

**Logs and status**

```bash
docker compose logs -f worker      # imports, matching, downloads
docker compose logs -f web
docker compose ps
```

**Back up.** What is irreplaceable: `SpankBank.sqlite` (your watches, favorites, settings), `device.sqlite` (what is
downloaded), and the media folder. `server.sqlite` can be rebuilt from the exports and the keys folder only holds
antiforgery keys. Do not copy the databases while the worker is writing: either stop the stack first
(`docker compose stop`) or take a consistent copy of each file with SQLite's online backup:

```bash
sqlite3 data/db/SpankBank.sqlite ".backup 'backup/SpankBank.sqlite'"
sqlite3 data/db/device.sqlite    ".backup 'backup/device.sqlite'"
```

Back up the media folder with any file tool (`rsync -a` is fine).

**Move the data.** `docker compose down`, move the folders, change the `SPANKBANK_*_DIR` values in `.env`,
`docker compose up -d`. If you replace or recreate a data folder *while the containers are running*, restart them
afterwards: a container keeps the folder it first mounted, so it would otherwise see an empty or deleted one and fail
with "unable to open database file".

**Start over.** Stop the stack and delete `data/db/*` to forget everything (including watches and favorites), or only
`data/db/server.sqlite` to rebuild the catalog at the next update. Media is never deleted by SpankBank except through the
bin.

## Archiving another e621ng site

The same images work for any site that runs [e621ng](https://github.com/e621ng/e621ng) and publishes the same database
exports, for example e6ai. Make a second stack (a copy of the compose file and `.env` in another directory, so it has
its own project name) with its own data folders and port, and set:

```bash
SITE_HOST=e6ai.net        # STATIC_HOST defaults to static1.<SITE_HOST>
SITE_NAME=e6ai            # picks the e6ai look (black and yellow theme and logo); other names get e621's look
SPANKBANK_WEB_PORT=8681
SPANKBANK_DB_DIR=./data/e6ai/db   # …and the same for the imports, keys and media folders
```

The downloader refuses any request for the site's hosts that ends up on a different host, and an export index that
points off-site is rejected, so a site that closes and redirects to another cannot pour that site's data into this
catalog. Adding another site's look takes one logo in `src/Web/Frontend/images/logo/`, one SCSS file in
`src/Web/Frontend/src/styles/sites/` and one line in `src/Core/SiteLook.cs`.

## Building and developing without Docker

You need the **.NET 10 SDK** and **Node 22**.

```bash
# the frontend (writes src/Web/wwwroot/dist, git-ignored)
(cd src/Web/Frontend && npm ci && npx vite build)

# everything else
dotnet build SpankBank.slnx
dotnet test tests/SpankBank.Tests
```

To run both processes on your machine against a local data folder:

```bash
mkdir -p data/db data/imports data/keys data/media
export SPANKBANK_DATA_DIR=$PWD/data
dotnet run --project src/Worker                      # terminal 1: the worker (command server on :8081)
WORKER_URL=http://localhost:8081/ ASPNETCORE_URLS=http://127.0.0.1:8621 \
  dotnet run --project src/Web                       # terminal 2: the site
```

Flash thumbnails need Ruffle's `exporter` binary at `/usr/local/bin/exporter` (the worker image puts it there); without it
everything else still works and only the Flash thumbnails are missing.
For frontend work, `npx vite build --watch` in `src/Web/Frontend` rebuilds on change. The regression tests cover the
import guard, run-request state, download accounting, favorites, search and watch-matching rules, and the site
configuration.

## Troubleshooting

| Symptom | Likely cause and fix |
|---|---|
| The web page answers 500 and the log says `unable to open database file` | The data folder is not writable by `SPANKBANK_UID:SPANKBANK_GID`, does not exist (Docker then creates it as root), or was recreated while the container ran (restart the stack). |
| `docker compose build` fails at `COPY --from=spankbank-ruffle-exporter:0.7.1` | The Flash helper image is missing (never built, or removed by an image prune). Build it again: `docker compose --profile tools build ruffle-exporter`. |
| Status says "The worker is not reachable" | The worker is not running or still starting: `docker compose ps`, `docker compose logs worker`. |
| Tags, artists and pools are empty | The catalog has not been imported yet: press *Update from e621 now* on **Status** and wait for it to finish. |
| Pages such as Status are very slow | The databases are on a slow disk or a network share. Move `SPANKBANK_DB_DIR` to an SSD. |
| Nothing is downloading | Check **Settings** (downloads on, daily limit reached?), that a watch is *enabled*, and **Posts → Downloads** for errors. |
| Wayback / Inkbunny recovery does nothing | They may not be reachable from your network or may be rate-limiting you; the worker backs off (up to an hour) and the log says so. |
| A form submit fails with a 400 after a redeploy | The browser holds an antiforgery token from the old key ring; reload the page. Keep `SPANKBANK_KEYS_DIR` persistent so it does not happen. |

## Project layout

```
src/Core/      domain models, search parser, site configuration (no UI or database types)
src/Data/      SQLite schema, repositories, export importer, matcher, downloader, rubbish bin
src/Worker/    the worker service: scheduler, download loop, bin, Flash previews, command server
src/Web/       ASP.NET Core Razor Pages app; Frontend/ holds the Vite + SCSS + TypeScript sources
tests/         xUnit regression tests
docker/        Dockerfiles for the worker, the web app and the Ruffle exporter
docs/          notes on code adapted from e621ng
```

## Credits and licence

SpankBank is released under the [MIT licence](LICENSE).

The web interface reproduces the markup, scripts and styles of [e621ng](https://github.com/e621ng/e621ng) (BSD-2-Clause);
adapted files carry a header comment and are covered by [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), which also
lists the upstream commit used ([docs/e621ng-upstream.md](docs/e621ng-upstream.md)). Flash playback uses
[Ruffle](https://ruffle.rs) (MIT / Apache-2.0), thumbnails are scaled with [SkiaSharp](https://github.com/mono/SkiaSharp)
(MIT). Thanks to the e621 team for publishing the database exports this project is built on, and please be kind to
their servers.
