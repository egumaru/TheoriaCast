---
name: theoriacast-visual
description: Use TheoriaCast Visual MCP to import PNG or JPEG assets, run image and inspection jobs, verify and download images, replay recipes, and prepare visual asset sets.
---

# Work with Visual assets

Connect to the Visual server using the `mcp_url` and Bearer token from its private connection file. Use the server that owns the asset or has a verified peer connection to it.

## Import, process and retrieve

1. Calculate the SHA-256 of the original PNG or JPEG. For files up to 384 KiB, call `visual_import_asset` with `request: {name, sha256, bytes}` and base64 bytes. For larger files, call `visual_stage_image_chunk` with `chunk: {bytes}` for each piece of up to 384 KiB and send `request: {name, sha256, chunks}` with the returned digests in order. The original file stays available. New processing recipes apply JPEG EXIF orientation.
2. Use `visual_estimate_batch` and `visual_submit_batch` with `request: {settings, inputs, hardware}`. Supply the asset's full `reference` (`serverId`, `assetId`, `sha256`) in `inputs` and use `cpu`, `gpu`, `cloud-gpu` or `auto`. For example, `settings: {operation: "resize", width: 1280, height: 720}` requests a center crop. Background editing and inspection require the matching models or inspection mode on the server.
3. Poll `visual_get_job` with the returned job ID. Read each item's `status`, `output` or `inspection` and any `error`. For a completed image, call `visual_read_image` with `reference: <output>, offset: 0, count: 393216`. Decode `bytes` and use the actual decoded byte count to advance `offset` to `totalBytes`. Check the saved SHA-256 against the response and the reference. Use `mediaType` to choose the extension.
4. Read the frozen processing provenance with `visual_get_recipe` or `visual_get_asset_recipe`. To repeat, call `visual_replay_recipe` with `request: {recipeId}`; use `request: {recipeId, change: {parameter, value}}` for one supported change. Compare output SHA-256 on the same runtime and settings.

## Review sets

Use `visual_create_set` with `request: {name, jobId}` or `request: {name, assets}`. Review the current version with `visual_get_set`, then use `visual_review_set_item` to record the user's decisions. Call `visual_approve_set` only when the user requests approval. Fetch the frozen cut order and recipes with `visual_get_set_manifest`; the approved ZIP is available in Atelier or via the authenticated API at `/api/v1/visual/sets/{id}/export`.

For karaoke video generation, use the [karaoke skill](../theoriacast-visual-karaoke/SKILL.md).
