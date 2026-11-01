# Better Download Manager (BDM) — Design

Status: DRAFT for owner approval. Nothing is built yet.

## Intent

A personal-use Windows download manager with the feature set of Internet Download Manager (IDM), plus a Chromium extension and a stream catcher that is more reliable than IDM's. The owner wants it because IDM is paid; it is for their own use, not for distribution.

Success = the owner can uninstall IDM and not miss it, and streams that IDM fails to catch (and that the "4saved" extension catches) get caught by BDM.

## Hard rules (anti-goals)

- **Clean-room.** BDM is built from public specs (HTTP/FTP RFCs, HLS RFC 8216, DASH ISO/IEC 23009-1, Chrome extension docs) and open-source behavior (aria2, yt-dlp, ffmpeg). Neither IDM's nor 4saved's code is decompiled, copied, or inspected. (Owner offered IDM code as a reference; declined by the builder — not needed for any feature below.)
- **No DRM circumvention.** Widevine/PlayReady/FairPlay streams are detected and shown as "protected, cannot download". No key extraction, no CDM tricks.
- **No licensing/cracking features**, no telemetry, no network calls except to download targets and (optionally) an update check the owner turns on.
- "Always catch the stream" means: every non-DRM stream the browser actually plays gets listed. It is a goal verified by a test corpus, not a guarantee for every site on earth.

## Constraints

- Windows 10/11 x64 only. .NET 8 SDK is installed; ffmpeg 8.1 is installed (BDM bundles/locates its own copy, see below).
- Chromium browsers only (Chrome, Edge, Brave, Opera, Vivaldi) via Manifest V3. Firefox is a non-goal for v1.
- Single user, no accounts, no server.

## Architecture (builder's calls, marked [call])

Four parts in one repo:

1. **`BDM.Core`** [call: C# class library, .NET 8] — download engine, no UI.
   - Segmented downloader: N parallel range-request connections (default 8, max 32), dynamic re-segmentation (a finished segment splits the largest remaining one), resume across restarts via a `.bdmpart` sidecar (segment map + ETag/Last-Modified validators), retry with backoff, redirect + cookie + Referer + custom-header passing, fallback to single connection when the server lacks range support.
   - Protocols: HTTP/HTTPS, FTP/FTPS (use `System.Net` + a small FTP client library), proxy (system / manual / auth).
   - Queue + scheduler: multiple queues, per-queue connection and concurrency limits, start/stop times, run-on-schedule, post-completion actions (open file, run program, sleep/hibernate/shutdown).
   - Speed limiter (global and per-download), disk preallocation, file categories with default folders, duplicate detection, post-download checksum (MD5/SHA-1/SHA-256) and optional antivirus hook (run a configured command on completion).
   - Stream engine: HLS (master/media playlists, AES-128 keys, byte-range, fMP4 init segments, live-to-VOD capture), DASH (static MPD, SegmentTemplate/Timeline/List/Base, multi-period), direct progressive media. Segments download through the same segmented engine; merge/remux via ffmpeg (`-c copy`), with quality picker and audio+video track pairing.
2. **`BDM.App`** [call: WPF, .NET 8] — the UI. IDM-style main window: category tree, download list (status, size, speed, ETA, added), toolbar, add-URL dialog with file-info probe, per-download progress dialog with segment view, completion dialog, system tray with clipboard monitor, drag-drop target, batch download (URL patterns like `file[001-100].jpg`), **site grabber** (crawl a page/site with depth + filters), site login manager, scheduler UI, options (connections, proxy, file types, folders, appearance). Dark/light theme via the owner's theme-studio tokens if available; otherwise plain system theme [call].
3. **`BDM.Host`** [call: small .NET console exe] — Chrome *native messaging host*. Speaks the length-prefixed JSON protocol on stdin/stdout, forwards to the running app over a named pipe, and launches the app if it is not running. Installed by registering a per-user registry key (`HKCU\Software\Google\Chrome\NativeMessagingHosts\…`, same for Edge/Brave) — no admin needed.
4. **`extension/`** [call: plain JavaScript, no build step, Manifest V3] — the browser side.

Local state: SQLite (`Microsoft.Data.Sqlite`) at `%AppData%\BetterDownloadManager\`. Logging to rolling text files.

## The stream catcher (the part that has to beat IDM)

IDM-style "watch URLs ending in .m3u8/.mp4" misses streams. The extension therefore runs layered detection, and every layer reports into one de-duplicated "found media" list per tab:

1. **Network layer** — `chrome.webRequest` (observe-only, `extraHeaders` for Referer/Cookie) matching by **response Content-Type and URL pattern and magic bytes** (a cheap `Range: bytes=0-15` probe for ambiguous responses), not by file extension alone. Catches `.m3u8`, `.mpd`, `.ts`/`.m4s`/`.mp4`/`.webm`/`.mp3`/`.aac`, and extensionless or query-string URLs.
2. **Page layer** — a content script injected in the `MAIN` world at `document_start`, in all frames (`all_frames`, `match_about_blank`), that wraps `fetch`, `XMLHttpRequest`, `URL.createObjectURL`, and `MediaSource.addSourceBuffer`/`appendBuffer`. Sees manifests and segment URLs requested by page JS, and detects blob/MSE playback that has no network URL of its own, by tracing back to the segment requests that fed it.
3. **DOM layer** — `<video>`, `<audio>`, `<source>`, `<track>` (subtitles), and `MutationObserver` for late additions, including inside open shadow roots.
4. **Worker layer** — requests made from dedicated/shared workers and service workers are attributed back to their tab via `webRequest` `tabId`/`initiator` and `chrome.runtime` messaging.
5. **Fallback layer (opt-in per site)** — `chrome.debugger` attached to the tab (Network domain) for sites that defeat layers 1–4. This shows Chrome's "debugging this browser" bar, so it is off by default and the owner enables it per site from the popup.

Context captured with every item: page URL, frame URL, tab title (for filename), all request headers that matter (Cookie, Referer, Origin, User-Agent, Authorization), and for HLS/DASH the full manifest so the app can re-fetch with identical headers. Short-lived signed URLs are downloaded immediately on click, with the headers captured at detection time.

**Verification corpus (part of the deliverable):** a local test site with HLS (clear + AES-128), DASH, MSE-blob, iframe-embedded, service-worker-fetched, and extensionless-URL players, plus a checklist of real sites the owner names where IDM fails. The detector is not "done" until every non-DRM case on the corpus is caught. The owner supplies the real-site list; builder cannot guess it.

## Extension UX

- Toolbar badge = count of media found on the current tab; popup lists items (name, type, resolution/bitrate, size, duration), with Download / Download with quality… / Copy URL.
- IDM-style floating "Download this video" button over playing video (toggle in options).
- Automatic takeover of browser downloads for configured file types/sizes (`chrome.downloads.onDeterminingFilename` → cancel browser download → hand to BDM), with a hold-Alt-to-skip override and a site exclude list.
- Context menu: "Download with BDM", "Download all links on page", "Download all images/media on page".
- If the app/host is unreachable the extension falls back to letting the browser download normally and shows a notice — never silently drops a download.

## Extra features beyond IDM (owner asked for suggestions)

Proposed for v1 unless the owner strikes them (all cheap given the architecture):
1. **yt-dlp integration** for site extractors (YouTube etc.) — BDM calls yt-dlp as an external tool when a page URL isn't a plain stream. Biggest coverage gain per line of code.
2. **Subtitle and thumbnail capture** alongside video; merge soft subtitles into mkv/mp4 on request.
3. **Browser cookie import** for authenticated downloads (via the extension, not by reading browser databases).
4. **Auto-retry on network change** / resume after sleep or Wi-Fi switch.
5. **Local REST/CLI** (`bdm add <url>`) bound to `127.0.0.1` only, token-protected, so scripts and other tools can queue downloads.
6. **Torrent/magnet support** — *not* in v1 (different protocol, large scope); listed for later.
7. **Download history search + export/import of settings and queues.**
8. **Surfshark VPN integration** allow user to channel specific or all downloads through Surfshark VPN
   - [call] Surfshark exposes no API or CLI for its Windows app, so BDM cannot connect/disconnect it or pick its servers. Feasible route: Surfshark's manual-connection **HTTPS/SOCKS5 proxy servers**, authenticated with the owner's Surfshark *service credentials* (found in their Surfshark account, not the login password). BDM stores them in Windows Credential Manager (DPAPI), offers a server picker, and lets the owner route a chosen download, a queue, a category, or everything through that proxy ("VPN profile" = a named proxy). Stream segment downloads inherit the profile.
   - Limits to expect: proxy-only (traffic from BDM, not the whole machine); the Surfshark app itself, if running a full VPN, already covers all traffic and needs no BDM support; server list is entered/imported by the owner since Surfshark publishes no stable machine-readable list [call].

## Delivery order (it is one product, built in this order, shippable at each step)

1. Core engine + minimal CLI harness (segmented, resume) — proves the foundation.
2. WPF app shell wired to the engine (list, add URL, progress, queues, tray).
3. Native host + extension: link capture and browser-download takeover.
4. Stream engine + stream catcher layers 1–3, then 4–5, run against the verification corpus.
5. Scheduler, batch, site grabber, logins, proxy, FTP, post-actions.
6. Extras (yt-dlp, REST/CLI, subtitles), installer (per-user MSI/MSIX or Inno Setup [call]), polish.

The owner chose "all at once"; the order above is the build sequence inside one plan, not a reduction of scope.

## Left to the builder

Library choices (FTP client, HLS/DASH parsing — hand-rolled parsers preferred over heavy deps), exact UI layout (modeled on IDM's familiar layout), installer technology, test framework (xUnit), icon/branding.

## Owner answers (2026-11-01)

1. Failing sites: **Instagram stories**; owner does not remember others. Corpus therefore starts with Instagram stories (logged-in session, signed short-lived media URLs fetched by page JS, video+audio often separate DASH tracks) plus the local test site; owner will add real sites as they hit them. Detection is not claimed "done" for sites not yet named. Instagram requires the owner's own logged-in session (cookies/headers passed from the browser); no login automation or scraping of other users' private content beyond what the owner's own browser can already see.
2. Extras: **all accepted**, including the owner-added Surfshark item.
3. Windows + Chromium only for v1: **accepted**.

## Still open

- Approval of this document as a whole (needed before the implementation plan is written).
- Surfshark: confirm the proxy-based approach in extra #8 is what they meant (vs. controlling the Surfshark app, which is not possible).
