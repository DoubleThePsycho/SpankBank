# e621ng upstream reference

Files under `src/Web` that were copied or adapted from https://github.com/e621ng/e621ng carry the upstream BSD-2 notice
(see `THIRD-PARTY-NOTICES.md`). Pinned upstream commit: `da51d7ae5d72efbae27c86852fa274f7c6689ff1` (2026-10-25).

| Local | Upstream |
|---|---|
| `src/Web/Frontend/src/styles/**` | `app/javascript/src/styles/**` (unchanged, except `base/_logos.scss`, which drops the logo set) |
| `src/Web/Frontend/fonts`, `images/themes` | `app/javascript/fonts`, `app/javascript/images/themes` |
| `src/Web/Frontend/src/js/pages/posts/SearchControls.ts`, `SearchFilters.ts`, `SearchQuery.ts` | `app/javascript/src/js/pages/posts/*` (unchanged) |
| `src/Web/Frontend/src/js/utility/{Offclick,Logger,SVGIcon}.ts`, `storage/**` | `app/javascript/src/js/utility/*` (unchanged) |
| `src/Web/Frontend/src/js/components/autocomplete/**` | `app/javascript/src/js/components/autocomplete/**` (unchanged, except `index.ts` and `providers/TagProvider.ts`: no CurrentUser/blacklist; `ArtistTagProvider.ts` and `CharacterTagProvider.ts` are new) |
| `src/Web/Frontend/src/styles/common/overlay_modal.scss` | `app/javascript/src/styles/common/overlay_modal.scss` (card styling also applies to `div.simple_form`) |
| `src/Web/Services/IconPaths.cs` | generated from `app/helpers/icon_helper.rb` (lucide icons) |
| `src/Web/Pages/Shared/_Layout.cshtml` | `app/views/layouts/default.html.erb`, `layouts/navigation/*`, `layouts/_theme_include.html.erb` |
| `src/Web/Pages/Shared/_SearchControls.cshtml` | `app/views/posts/partials/index/_controls.html.erb` |
| `src/Web/Pages/Posts/Index.cshtml` | `app/views/posts/index.html.erb`, `components/post_thumbnail_component.html.erb`, `components/paginator_component.html.erb` |

To update: diff the upstream files against the local copies and re-apply. The site's logo, landing page and mascots are intentionally not ported.

The autocomplete and modal files were copied from a local e621ng checkout (commit `b548347e5e5ca06472f8d6abfe642b816d67583b`), which is newer than the pinned commit above. The watches pages (`src/Web/Pages/Watches`) and `styles/specific/watches.scss` are SpankBank's own.

`js/core/Navigation.ts` is a trimmed copy (main-menu toggle only). `images/logo/e621.svg` is e621's logo shape with the lettering replaced by "SB" (`e6ai.svg` is the e6ai look). `common/container_local.scss` (Bootstrap container sizing) and `common/layout_local.scss` are SpankBank's own.
`js/core/PaginatorFastTravel.ts` is a copy (only the jquery import was added).

The Tags, Artists and Pools pages (`src/Web/Pages/{Tags,Artists,Pools}`) follow e621ng's `tags/index`, `artists/index` and `pools/index`/`show` views, adapted to the local catalog.

Favorite folders (nested, one folder per favorite) are ported from e621ng pull request 2572 (https://github.com/e621ng/e621ng/pull/2572, by the SpankBank author): `js/pages/posts/FavoriteFolderDrag.ts` (adapted: own endpoints, no CSRF token), `FavoriteFolderOverlay.ts` (rebuilt on the shared `.st-overlay` classes, no ImmersiveInput), `FavoriteFolders.ts` (replaces `models/FavoriteFolder.ts`), the folder card styles in `styles/specific/favorites.scss`, and the folder rules in `Data/Repositories/FavoriteFolders.cs` (`favorite_folder_manager.rb`, `favorite_folder.rb`). The post toolbar (`js/pages/posts/PostToolbar.ts`, markup in `Pages/Posts/Show.cshtml`) follows `PostsShowToolbar.js` and `posts/partials/show/content/_toolbar.html.erb` at the pinned commit.

The advanced search's Media type and Sound filters are e621ng pull request 2516 (https://github.com/e621ng/e621ng/pull/2516, by the SpankBank author), applied on top of the pinned files: `SearchFilters.ts`, `SearchQuery.ts`, `_settings.scss`, the two-column `Pages/Shared/_SearchControls.cshtml` and the new icons in `IconPaths.cs`. They write tag groups like `( ~video ~flash )`, so the local search parser (`Core/Search/PostSearchQuery.cs`) understands parenthesised OR groups (`AnyOfGroups`) in the posts search, the blacklist and search watches. The PR's own JS tests are not ported (the frontend has no test runner).
