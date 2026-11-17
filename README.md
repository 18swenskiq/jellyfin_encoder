# Jellyfin Space Saver

A Jellyfin **10.11.8** plugin for converting local library videos to **H.265 / HEVC** using **Intel Quick Sync or VA-API on Linux**. Designed for an Ubuntu host running Jellyfin in Docker.

Open **Dashboard → Plugins → Space Saver → Settings** (also exposed as a menu page). Browse non-HEVC files, search, sort by file size/name/codec, and convert one file or a selection. The queue runs one encode at a time and shows progress, errors, cancellation, original/output sizes, and backup paths.

## Install from the Jellyfin catalog

After the first GitHub release is published, add this repository URL in **Dashboard → Plugins → Repositories → Add**:

```text
https://github.com/18swenskiq/jellyfin_encoder/releases/latest/download/manifest.json
```

Give it a name such as **Space Saver**, save it, then find **Space Saver** in the **Catalog**, install it, and restart Jellyfin. Open the plugin settings and run **Test GPU**. Later releases are available through Jellyfin's plugin update mechanism using the same repository URL. GPU device access and writable media mounts are still required; see below.

The URL starts working only after a successful **Publish plugin** workflow run. GitHub Pages and custom GitHub credentials in Jellyfin are not needed. The GitHub repository must be public.

## Publish the first GitHub release

1. Commit and push this folder, including `.github/workflows/release.yml` and the scripts, to [18swenskiq/jellyfin_encoder](https://github.com/18swenskiq/jellyfin_encoder). Build outputs and NuGet caches are ignored and should not be committed.
2. On GitHub, open **Actions → Publish plugin → Run workflow**, select `main`, then run it. The workflow must be on the default branch for this button to appear. It uses the built-in `GITHUB_TOKEN`; no personal token or repository secret is required. Repository/organization rules must allow Actions to write releases.
3. Wait for the workflow to finish. It builds and tests the plugin, creates the `v0.1.0.0` release, and publishes its ZIP, `manifest.json`, and `SHA256SUMS`. The workflow summary includes the Jellyfin repository URL.
4. Add that URL in Jellyfin as described above.

The catalog manifest is generated from `meta.json` and the actual release ZIP; **`meta.json` itself is not the repository manifest**. Jellyfin requires an MD5 checksum of the ZIP, and the workflow also publishes SHA-256 for independent verification. A draft release holds all assets until upload succeeds, then becomes the latest stable release. Older catalog entries are carried forward so servers can still find compatible versions.

For each update, increment the four-part version in both `meta.json` and `Jellyfin.Plugin.SpaceSaver/Jellyfin.Plugin.SpaceSaver.csproj`, push your changes, and run **Publish plugin** again. Alternatively, push a matching tag, for example `v0.1.1.0`, to trigger it. Keep the plugin GUID unchanged. If you change the Jellyfin package references, update `targetAbi` to match. Published versions cannot be overwritten; use a new version. A failed asset upload leaves a draft that can be retried from the same commit. Do not publish unrelated stable releases without a catalog manifest in this repository, since they would replace the `latest` link.

## Install the packaged plugin manually

The local build is in `artifacts/SpaceSaver_0.1.0.0.zip`. It contains the plugin DLL and `meta.json`; do not install Jellyfin's own dependency DLLs from NuGet alongside it.

1. Extract the ZIP into a directory named `SpaceSaver_0.1.0.0` beneath your server's **plugins directory**, in its persistent configuration/data volume. The official image commonly uses `/config/plugins`; installations with a different `JELLYFIN_DATA_DIR` may use a different directory. Check your existing plugin folders / server startup paths first.
2. Restart the Jellyfin container.
3. Open Space Saver in the plugin settings, save the encoder/device settings, and click **Test GPU**.
4. Convert a representative file, check playback and track selection, then click **Delete original backup** for that job to reclaim the space. This final action is permanent and has an explicit confirmation.

Example when the container is named `jellyfin` and its plugin directory is `/config/plugins`:

```sh
unzip SpaceSaver_0.1.0.0.zip -d SpaceSaver_0.1.0.0
docker cp SpaceSaver_0.1.0.0 jellyfin:/config/plugins/
docker restart jellyfin
```

Ensure the container's user can read the DLL and write to both its plugin data directory and media directories. Media mounts must be writable to create temporary outputs and replace files.

## Intel GPU in Docker

Use the official Jellyfin image with its bundled `jellyfin-ffmpeg`. The plugin obtains FFmpeg and FFprobe paths from Jellyfin's `IMediaEncoder`; it does not download or substitute them. The plugin's device/backend settings are independent of Jellyfin playback-transcoding settings.

Merge GPU access into your **existing** Compose service (preserve its current volumes, ports and other settings):

```yaml
services:
  jellyfin:
    devices:
      - /dev/dri:/dev/dri
    group_add:
      - "${RENDER_GID}"
```

Set `RENDER_GID` to the numeric owning group of the render node on the Ubuntu host:

```sh
stat -c '%g' /dev/dri/renderD128
docker exec jellyfin ls -l /dev/dri
docker exec jellyfin /usr/lib/jellyfin-ffmpeg/vainfo --display drm --device /dev/dri/renderD128
```

Recreate the container after Compose changes. Some deployments additionally need the video group or device-specific permissions. Consult Jellyfin's [Intel GPU setup documentation](https://jellyfin.org/docs/general/post-install/transcoding/hardware-acceleration/intel/) for the actual GPU generation and driver requirements. HEVC encode support is required; not every Intel GPU supports it, and 10-bit support varies.

Default: **QSV**, `/dev/dri/renderD128`, quality **24**, hardware decoding **on**, minimum savings **5%**. Use **VA-API** if QSV is unavailable on your hardware/driver combination. Lower quality numbers retain more video detail and usually produce larger files. These values are encoder controls, not equivalent quality scores across backends. GPU encoding is fast but does not guarantee better compression than a slower CPU encode.

The GPU test performs a short HEVC encode through the chosen render device. It proves encoder access; each source still has to use a supported decoder/profile. If full hardware decoding fails for a source codec, turn hardware decoding off and enqueue it again for CPU decoding + GPU encoding. There is no silent fallback or automatic retry.

## Preservation and replacement

- Explicitly maps **every input stream**, copies non-video streams, and re-encodes only the single main video stream. Copies audio/subtitle codecs, languages, titles, disposition flags (including default/forced), attachments, metadata and chapters. Subtitles are not burned in. See FFmpeg's [stream selection and stream-copy documentation](https://ffmpeg.org/ffmpeg.html).
- Keeps MKV, MP4 and MOV containers. Other inputs target MKV with the same basename; external subtitle/metadata/image sidecars remain untouched. Incompatible stream/container combinations fail rather than silently removing or converting tracks. Sidecars that encode the old extension in their filename may need manual renaming after a container change.
- Writes a hidden `.spacesaver-<id>.tmp` file next to the source. Needs approximately the source size plus 256 MiB of free space before starting; actual output requirements can vary.
- Uses FFprobe to check stream counts/codecs, audio parameters, track metadata/dispositions, video resolution/bit depth/color metadata, chapter titles/timing, and duration. Then decodes the complete output video with FFmpeg to detect errors. This validation pass uses CPU decoding; hashing and muxing also use CPU/I/O even with a fully accelerated encoding pipeline.
- Rejects outputs that are larger or fail the configured savings threshold. Originals remain in place on encode/validation failure or cancellation.
- Hashes the original before and after encoding to detect changes. Checks active Jellyfin playback before encoding and before replacement. These checks do not lock out external media managers; avoid concurrent file edits/moves during conversion.
- Persists job state before replacement. Same-container replacement uses the filesystem replacement operation with a backup. Container changes move the original to its backup and roll back if installation fails. Existing destination/backup files are never intentionally overwritten.
- Retains the source as `<original filename>.spacesaver-original`, a non-media suffix. Disk usage temporarily grows until you delete this backup from the job card. Deletion verifies SHA-256 hashes of both the retained original and installed output first.
- Notifies Jellyfin's library monitor after installation. If filesystem monitoring is disabled, run a library scan manually. Changing a container/path can create a new Jellyfin item ID; it may affect watch history or external automation referencing the old path.
- All API endpoints require Jellyfin administrator authorization. Requests use library item IDs, not user-supplied filesystem paths or shell commands. FFmpeg arguments use `ProcessStartInfo.ArgumentList` with no shell.

## Current limits

HDR / Dolby Vision, detected embedded video closed captions, interlaced video, non-4:2:0 pixel formats, multi-angle video, multi-part items, and source-file symlinks are rejected rather than converted with unverified presentation changes. AV1 and VP9 appear because they are not HEVC, but converting already efficient codecs may save nothing. The library list uses Jellyfin's stored codec information; each conversion re-probes the actual source before encoding. Missing/offline/remote files and disc directories are excluded.

This is a first implementation. It has been compiled against 10.11.8 and exercised with generated MKV/MP4 media on the development host. The actual Linux Intel GPU path and Jellyfin web UI still require validation in your container. Hardware flags follow QSV/VA-API device derivation; GPU generation, drivers and Jellyfin FFmpeg builds can affect support. Filesystem durability semantics vary, especially on network storage; retained originals are a recovery aid, not a substitute for independent backups.

## Interrupted jobs and recovery

Job history is stored under `<Jellyfin data directory>/spacesaver/jobs.json`. Queued/in-progress jobs become **Interrupted** after a server restart and are not automatically resumed. Source, output, backup and temporary paths are shown on each job card. Interrupted temporary files are deliberately retained for inspection.

Stop conversions and inspect those paths before manual recovery. If the original source is missing but its `.spacesaver-original` exists, move the backup back to the original name. If a converted output already exists, move that output aside first; do not overwrite either file blindly. Restore the original extension when rolling back a container change. Remove an abandoned temporary file only after verifying the original is present, then rescan the library. A crash during backup deletion may leave an interrupted job even if deletion finished; check the listed files. Do not edit the journal while Jellyfin is running.

## Build and verify

Requires a .NET SDK supporting `net9.0` (9 or newer), and FFmpeg/FFprobe on PATH for integration checks.

```sh
dotnet build Jellyfin.Plugin.SpaceSaver/Jellyfin.Plugin.SpaceSaver.csproj -c Release
dotnet run --project tests/SpaceSaver.Tests.csproj -c Release
node --check Jellyfin.Plugin.SpaceSaver/Configuration/configPage.js
```

Package with `pwsh -File scripts/package.ps1` on Windows or `sh scripts/package.sh` on Linux (requires Python 3). Both generate the ZIP, `manifest.json`, and `SHA256SUMS` under `artifacts/`. A locally generated manifest points to the future GitHub release; it is not usable until those release assets are published. Validate packaging/publication logic with `python -m unittest discover -s tests -p 'test_release*.py' -v`; publication tests use mocked GitHub calls and never create releases.

The test executable validates command construction and rejection rules, administrator policy, safe replacement/rollback, cancellation, and real MKV/MP4 transcodes with multiple audio tracks, forced subtitles and chapters (plus an MKV attachment). Audio/subtitle packet hashes are compared before and after. Integration tests replace only GPU-specific encoding options with software x265 so they run without a GPU; they do **not** certify hardware compatibility. Generated fixtures stay under `artifacts/test-media` for inspection.
