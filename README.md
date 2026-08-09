# Bloom

Semantic, natural-language search for Jellyfin.

Ask for what you mean, not just what a title is called: *"80s horror but funny"*,
*"movies like Heat"*, *"rated PG under 90 minutes"*. Bloom intercepts Jellyfin's
search and answers it with keyword + meaning-aware ranking, then falls back
cleanly to fast keyword search when the extras aren't configured.

Bloom is a soft fork of [arnesacnussem's Meilisearch plugin](https://github.com/arnesacnussem/jellyfin-plugin-meilisearch),
which it builds on for the keyword layer.

## How it works

Bloom decorates Jellyfin's item repository, so any client that searches through
`/Items` is covered with no client changes. A query is answered by the first
tier that can:

1. **Gateway** (recommended) — a standalone Bloom search service owns intent
   parsing, keyword + semantic fusion, enrichment phrases and reranking.
2. **In-plugin semantic** (fallback) — hybrid keyword + vector search run
   directly against Meilisearch using an embedding sidecar.
3. **Keyword** — plain Meilisearch (typo tolerance, prefix matching). Always the
   floor: if the gateway is down or nothing semantic is configured, search still
   works.

Your library is indexed into Meilisearch so results are your actual items, with
permissions and user filters preserved.

## Requirements

- Jellyfin `10.11.x`
- A [Meilisearch](https://www.meilisearch.com/) instance (required for the
  keyword layer and the library index)
- Optional, for semantic search: an embedding sidecar and/or a Bloom gateway

## Install

1. Jellyfin Dashboard -> Plugins -> Repositories -> add:
   ```
   https://raw.githubusercontent.com/Kisokumar/bloom-plugin/refs/heads/main/manifest.json
   ```
2. Catalog -> install **Bloom** -> restart Jellyfin.

## Configure

Open the **Bloom** plugin page. Everything is on one page, with a Test button and
live status for each connection:

- **Search gateway** — the Bloom gateway URL (leave empty to use the fallback).
- **Meilisearch** — URL, API key, index name, matching strategy. Required.
- **In-plugin semantic** — sidecar URL and embedding model, used only when no
  gateway is set.

Clearing the gateway URL reverts to in-plugin search instantly; disabling
semantic search reverts to pure keyword search.

The **Bloom Diagnostics** page has a search playground (compare keyword vs
semantic vs gateway), coverage status, and analytics.

## Environment variables

The Meilisearch connection can also be set without the UI: `MEILI_URL` and
`MEILI_MASTER_KEY`.

## License

GPLv3. See [LICENSE](LICENSE). Soft fork of arnesacnussem's Meilisearch plugin.
