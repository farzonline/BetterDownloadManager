# Better Download Manager Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build BDM, a personal-use Windows IDM-equivalent with a Chromium extension and a multi-layer stream catcher.

**Architecture:** `BDM.Core` (engine, no UI) is consumed by `BDM.App` (WPF), `BDM.Cli` (`bdm` command + REST), and `BDM.Host` (native-messaging bridge to the app over a named pipe). A plain-JS Manifest V3 `extension/` detects media in five layers and hands items to the host.

**Tech Stack:** .NET 8, WPF, xUnit, Microsoft.Data.Sqlite, FluentFTP, ffmpeg (external), yt-dlp (external), Node `node --test` for extension logic, Playwright (dev-only) for the acceptance corpus, Inno Setup.

**Spec:** `docs/superpowers/specs/2026-11-01-better-download-manager-design.md`

## Global Constraints

- Windows 10/11 x64 only; .NET 8; Chromium browsers only via Manifest V3 (no Firefox).
- Clean-room: no decompiling, copying or inspecting IDM or the 4saved extension. No DRM circumvention: Widevine/PlayReady/FairPlay streams are listed as "protected, cannot download".
- No telemetry; network calls only to download targets (optional update check off by default).
- Segmented downloads: default 8 connections, max 32; dynamic re-segmentation; resume via `.bdmpart` sidecar holding segment map + ETag/Last-Modified.
- State in SQLite at `%AppData%\BetterDownloadManager\`; logs are rolling text files there.
- HLS/DASH parsers are hand-rolled (no parsing dependency); merge with ffmpeg `-c copy`.
- Native messaging host registered per user under `HKCU\Software\<Browser>\NativeMessagingHosts\` (no admin); extension ID pinned with a manifest `key`.
- REST/CLI binds `127.0.0.1` only and requires a token.
- Surfshark = named HTTPS/SOCKS5 proxy profiles with the owner's service credentials; no control of the Surfshark app.
- Test framework xUnit; extension logic tested with `node --test`.
- Commit steps: executor appends the repo's configured attribution trailer.

## Review Focus

Failure modes the spec implies but never states; each is pinned by a named test in the owning task.

1. Server ignores `Range` (200 instead of 206), including on resume → restart cleanly, never corrupt (Task 4).
2. Filenames: illegal Windows characters, reserved names (`CON`), >260-char paths, name already exists (Task 1).
3. Signed URL expires mid-download (403) → keep sidecar, mark resumable-with-new-URL, do not delete progress (Task 4).
4. Live HLS (no `#EXT-X-ENDLIST`) → capture stops on user stop or ENDLIST and still produces a playable file (Task 15).
5. App not running or disk full when a download arrives → host launches app / extension falls back to browser download; disk-full is reported as such (Tasks 4, 9, 10).

---

### Task 1: Solution scaffold, probe, file naming

**Files:**
- Create: `BetterDownloadManager.sln`, `src/BDM.Core/BDM.Core.csproj`, `tests/BDM.Core.Tests/BDM.Core.Tests.csproj`, `src/BDM.Core/Http/DownloadRequest.cs`, `src/BDM.Core/Http/Prober.cs`, `src/BDM.Core/Files/FileNaming.cs`, `tests/BDM.Core.Tests/TestServer.cs` (in-process Kestrel fixture: configurable Range support, ETag, Content-Disposition, 403-after-N-bytes, random body)
- Test: `tests/BDM.Core.Tests/ProberTests.cs`, `FileNamingTests.cs`

**Interfaces:**
- Produces: `record DownloadRequest(Uri Url, string? FileName, string? Referer, IReadOnlyDictionary<string,string> Headers, string? CookieHeader, string? ProxyProfileId, string? Category, string DestinationDir)`; `record FileProbe(long? Length, bool AcceptsRanges, string? ETag, string? LastModified, string FileName, string? ContentType)`; `static Task<FileProbe> Prober.ProbeAsync(HttpClient http, DownloadRequest req, CancellationToken ct)`; `static string FileNaming.Sanitize(string name)`; `static string FileNaming.Unique(string dir, string name)`; `static string FileNaming.Resolve(string? contentDisposition, Uri url, string? contentType)`.

- [ ] **Step 1:** `git init`, create the solution and the two projects (`net8.0`; tests reference xUnit), add `.gitignore` (bin/obj/node_modules), commit.
- [ ] **Step 2: Failing tests.** `ProberTests`: `Probe_ReadsLengthAndRanges` (server with ranges → `AcceptsRanges==true`, `Length==N`); `Probe_FallsBackToRangeGetWhenHeadRejected` (HEAD→405 → still returns Length); `Probe_NoRangeSupport_AcceptsRangesFalse`. `FileNamingTests` (Review Focus 2): `Sanitize("a<b>:c?.mp4")=="a_b__c_.mp4"`; `Sanitize("CON.txt")=="_CON.txt"`; `Sanitize` of a 300-char name keeps the extension and result ≤ 200 chars; `Unique(dir,"a.zip")` returns `"a (1).zip"` when `a.zip` exists; `Resolve("attachment; filename*=UTF-8''%E2%82%AC.pdf", …)=="€.pdf"`; `Resolve(null, new Uri("http://x/y/video?id=3"), "video/mp4")=="video.mp4"`.
- [ ] **Step 3:** Run `dotnet test --filter "FullyQualifiedName~ProberTests|FullyQualifiedName~FileNamingTests"`. Expected: FAIL (types missing).
- [ ] **Step 4:** Implement the signatures above. Prober: HEAD first, then `GET` with `Range: bytes=0-0` on failure/405; `AcceptsRanges` true only on 206 or `Accept-Ranges: bytes`.
- [ ] **Step 5:** Re-run the same filter. Expected: PASS. Commit.

### Task 2: Segment map and sidecar

**Files:** Create `src/BDM.Core/Engine/Segment.cs`, `SegmentMap.cs`, `SegmentSidecar.cs`; Test `tests/BDM.Core.Tests/SegmentMapTests.cs`

**Interfaces:**
- Produces: `class Segment { long Start; long End /*inclusive, mutable*/; long Downloaded; long Remaining => End-Start+1-Downloaded; }`; `class SegmentMap { long Length; IReadOnlyList<Segment> Segments; static SegmentMap Create(long length, int n); bool TrySplitLargest(long minSize, out Segment created); bool IsComplete; }`; `static class SegmentSidecar { static void Save(string path, SegmentMap map, string? etag, string? lastModified); static (SegmentMap Map, string? ETag, string? LastModified)? Load(string path); }`.

- [ ] **Step 1: Failing tests.** `Create_CoversWholeFileWithoutGaps` (for lengths 1, 7, 10_000_000_001 with n=8: segments contiguous, sum==length — covers >4 GB); `Split_HalvesLargestRemaining` (new segment starts at old mid, old End shrinks, union unchanged); `Split_RefusesBelowMinSize`; `Sidecar_RoundTripsProgressAndValidators`; `Sidecar_CorruptFile_ReturnsNull`.
- [ ] **Step 2:** Run `--filter FullyQualifiedName~SegmentMapTests`. Expected: FAIL.
- [ ] **Step 3:** Implement (use `long` everywhere; sidecar is JSON written to temp then atomically moved).
- [ ] **Step 4:** Re-run. Expected: PASS. Commit.

### Task 3: Speed limiter and checksum

**Files:** Create `src/BDM.Core/Engine/SpeedLimiter.cs`, `Files/Checksum.cs`; Test `SpeedLimiterTests.cs`, `ChecksumTests.cs`

**Interfaces:** Produces `class SpeedLimiter { SpeedLimiter(long bytesPerSec); ValueTask AcquireAsync(int bytes, CancellationToken ct); void SetLimit(long bytesPerSec); }` (token bucket, shared across connections); `static Task<string> Checksum.ComputeAsync(string path, HashAlgorithmName alg)` returning lowercase hex.

- [ ] **Step 1: Failing tests.** `Acquire_ThrottlesToLimit` (500 KB through a 250 KB/s limiter takes ≥1.8 s and ≤3 s); `Acquire_Cancels` (token cancel throws `OperationCanceledException`); `Checksum_Sha256_OfKnownBytes` (`"abc"` → `ba7816bf…15ad`); `Checksum_Md5_OfKnownBytes`.
- [ ] **Step 2:** Run filter; FAIL. **Step 3:** Implement. **Step 4:** PASS. Commit.

### Task 4: Segmented downloader

**Files:** Create `src/BDM.Core/Engine/SegmentedDownloader.cs`, `DownloadOptions.cs`, `DownloadProgress.cs`, `DownloadException.cs`; Test `SegmentedDownloaderTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3 types.
- Produces: `record DownloadOptions(int Connections = 8, int MaxRetries = 5, long? SpeedLimitBytesPerSec = null)`; `record DownloadProgress(long Downloaded, long? Total, double BytesPerSec, IReadOnlyList<Segment> Segments)`; `record DownloadResult(string Path, long Bytes)`; `enum DownloadFailure { Network, AuthExpired, DiskFull, ServerChanged }`; `class DownloadException : Exception { DownloadFailure Kind; static DownloadException FromIo(IOException e) }`; `class SegmentedDownloader { SegmentedDownloader(HttpClient http, SpeedLimiter? limiter = null); Task<DownloadResult> RunAsync(DownloadRequest req, FileProbe probe, DownloadOptions opts, IProgress<DownloadProgress>? progress, CancellationToken ct); }`. Downloads to `<name>.bdmpart` data + sidecar, preallocates, renames on completion.

- [ ] **Step 1: Failing tests.** `Downloads_RandomFile_ByteIdentical_With8Connections`; `Resume_AfterCancel_ContinuesAndIsByteIdentical`; `Resume_RejectsChangedETag_RestartsFromZero` (Kind `ServerChanged` handled by restart); `NoRangeSupport_FallsBackToSingleStream` ; `ServerIgnoresRange_On200_RestartsCleanlyNoCorruption` (Review Focus 1, including during resume); `Expired403_MidDownload_KeepsSidecarAndThrowsAuthExpired` (Review Focus 3: `.bdmpart` and sidecar still exist, `Kind==AuthExpired`); `Retries_TransientFailure_ThenSucceeds` (500 twice then OK); `DynamicSplit_FinishedSegmentTakesOverSlowTail` (slow last segment gets split); `FromIo_HResult0x70_IsDiskFull` (Review Focus 5); `SpeedLimit_IsHonored`.
- [ ] **Step 2:** Run `--filter FullyQualifiedName~SegmentedDownloaderTests`. FAIL.
- [ ] **Step 3:** Implement per signatures. Send `Referer`, `Cookie`, custom headers on every range request; `If-Range` with ETag on resume.
- [ ] **Step 4:** PASS. Commit.

### Task 5: Store, download manager, queues

**Files:** Create `src/BDM.Core/Data/DownloadStore.cs`, `Data/DownloadRecord.cs`, `Engine/DownloadManager.cs`, `Engine/QueueDefinition.cs`; Test `DownloadStoreTests.cs`, `DownloadManagerTests.cs`

**Interfaces:**
- Consumes: Task 4.
- Produces: `enum DownloadState { Queued, Running, Paused, Completed, Failed }`; `record DownloadRecord(Guid Id, DownloadRequest Request, DownloadState State, long Downloaded, long? Total, string? Error, string QueueId, DateTime Added)`; `record QueueDefinition(string Id, string Name, int MaxConcurrent, int Connections, TimeSpan? StartAt, TimeSpan? StopAt, PostAction OnFinish)` (`PostAction` enum: `None, OpenFile, RunProgram, Sleep, Hibernate, Shutdown`); `class DownloadStore { DownloadStore(string dbPath); void Upsert(DownloadRecord r); DownloadRecord? Get(Guid id); IReadOnlyList<DownloadRecord> Search(string text); IReadOnlyList<QueueDefinition> Queues(); void SaveQueue(QueueDefinition q); string Export(); void Import(string json); }`; `class DownloadManager { DownloadManager(DownloadStore s, Func<string?, HttpClient> httpFor); Guid Add(DownloadRequest r, string queueId = "main"); void Pause(Guid id); void Resume(Guid id); void Remove(Guid id, bool deleteFile); event Action<DownloadRecord> Changed; }`.

- [ ] **Step 1: Failing tests.** `Store_RoundTripsRecord`; `Store_Search_MatchesNameAndUrl`; `Store_ExportImport_RestoresQueuesAndDownloads`; `Manager_RespectsQueueMaxConcurrent` (3 added, MaxConcurrent 2 → max 2 Running); `Manager_PauseThenResume_Completes`; `Manager_DuplicateUrl_FlagsDuplicate`; `Manager_Completion_VerifiesChecksumWhenProvided`; `Manager_AuthExpired_SetsFailedWithResumableMarker`.
- [ ] **Step 2:** Run both filters; FAIL. **Step 3:** Implement (SQLite via Microsoft.Data.Sqlite; schema created on open). **Step 4:** PASS. Commit.

### Task 6: Scheduler, post-actions, connectivity resume

**Files:** Create `src/BDM.Core/Engine/Scheduler.cs`, `PostActions.cs`, `ConnectivityWatcher.cs`; Test `SchedulerTests.cs`

**Interfaces:** Produces `static bool Scheduler.IsWindowOpen(QueueDefinition q, DateTime now)` (handles windows crossing midnight; null start/stop = always open); `static void PostActions.Run(PostAction a, string? path, string? program)` (behind `IPowerApi` seam for tests); `class ConnectivityWatcher { event Action NetworkRestored; }` (wraps `NetworkChange.NetworkAvailabilityChanged` and `SystemEvents.PowerModeChanged` Resume); `DownloadManager` gains `void ResumeNetworkFailures()` subscribed to `NetworkRestored`.

- [ ] **Step 1: Failing tests.** `Window_Overnight_23_to_06_OpenAt0030_ClosedAt1200`; `Window_Null_AlwaysOpen`; `PostAction_Shutdown_CallsPowerApiOnce`; `Manager_NetworkRestored_ResumesNetworkFailedDownloads_NotAuthExpired`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement. **Step 4:** PASS. Commit.

### Task 7: Proxy profiles and Surfshark

**Files:** Create `src/BDM.Core/Net/ProxyProfile.cs`, `CredentialVault.cs`, `HttpClients.cs`, `ProxyRouting.cs`, `SurfsharkImport.cs`; Test `ProxyTests.cs`

**Interfaces:** Produces `enum ProxyKind { Http, Socks5 }`; `record ProxyProfile(string Id, string Name, ProxyKind Kind, string Host, int Port, string? Username)`; `class CredentialVault { CredentialVault(string path); void Save(string key, string secret); string? Load(string key); }` (DPAPI `ProtectedData`, current user; [call] stored in an encrypted file under `%AppData%`, not Windows Credential Manager); `static HttpClient HttpClients.Create(ProxyProfile? p, CredentialVault v)`; `static string? ProxyRouting.Resolve(DownloadRequest r, QueueDefinition q, string? categoryProfile, string? globalProfile)` (precedence download > queue > category > global); `static IReadOnlyList<ProxyProfile> SurfsharkImport.Parse(string text)` (lines `host:port` or `name,host,port`; blank/`#` lines skipped).

- [ ] **Step 1: Failing tests.** `Vault_RoundTrips_AndFileIsNotPlaintext`; `Routing_PrecedenceDownloadOverQueueOverCategoryOverGlobal`; `Create_Socks5Profile_RoutesThroughLocalFakeProxy` (tiny SOCKS5 stub records the CONNECT host); `Create_WrongCredentials_Surfaces407AsNetworkFailureWithMessage`; `Import_ParsesBothLineFormats_SkipsComments`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement. **Step 4:** PASS. Commit.

### Task 8: FTP, batch patterns, site grabber

**Files:** Create `src/BDM.Core/Ftp/FtpDownloader.cs`, `Batch/BatchPattern.cs`, `Grabber/SiteGrabber.cs`; Test `BatchPatternTests.cs`, `SiteGrabberTests.cs`, `FtpDownloaderTests.cs` (in-memory FTP stub or skipped-with-reason if no stub library)

**Interfaces:** Produces `static IReadOnlyList<Uri> BatchPattern.Expand(string pattern)` (`[001-100]` zero-padded, `[a-c]`, multiple groups multiply); `record GrabOptions(int Depth, string[] IncludeExt, string[] ExcludeExt, bool SameHostOnly)`; `static Task<IReadOnlyList<Uri>> SiteGrabber.CrawlAsync(Uri start, GrabOptions o, HttpClient http, CancellationToken ct)`; `class FtpDownloader { Task<DownloadResult> RunAsync(DownloadRequest r, IProgress<DownloadProgress>? p, CancellationToken ct); }` (FluentFTP; supports FTPS and resume via REST).

- [ ] **Step 1: Failing tests.** `Expand("http://x/f[001-003].jpg")` → `f001,f002,f003`; `Expand("x[a-b]y[1-2]")` → 4 URLs; `Expand` without brackets → itself; `Crawl_DepthOne_FindsLinkedFilesFilteredByExt` (local test server pages); `Crawl_SameHostOnly_SkipsExternal`; `Crawl_DoesNotLoop_OnCircularLinks`; `Ftp_ResumesFromPartialFile` (stub).
- [ ] **Step 2:** FAIL. **Step 3:** Implement. **Step 4:** PASS. Commit.

### Task 9: Pipe protocol and native messaging host

**Files:** Create `src/BDM.Core/Ipc/PipeProtocol.cs`, `src/BDM.Host/BDM.Host.csproj`, `Program.cs`, `NativeMessaging.cs`, `AppLauncher.cs`, `src/BDM.Host/HostRegistrar.cs`, `tools/register-host.ps1`; Test `tests/BDM.Host.Tests/NativeMessagingTests.cs`, `HostRegistrarTests.cs`

**Interfaces:**
- Produces: `record HostMessage(string Op, string? Url, string? PageUrl, string? Title, Dictionary<string,string>? Headers, string? Cookie, string? ManifestBody, string? Kind)` with `Op ∈ {"ping","add","addStream"}`; `record HostReply(bool Ok, string? Error, bool AppStarted)`; `static class NativeMessaging { static Task<string?> ReadAsync(Stream s) /*4-byte LE length + UTF-8 JSON, null on EOF*/; static Task WriteAsync(Stream s, string json); }`; `static class AppLauncher { static Task EnsureRunningAsync(string pipeName, string appExe, TimeSpan timeout); }`; `static class HostRegistrar { static void Register(string hostExe, string extensionId, string[] browsers); }` writing the manifest JSON and `HKCU\Software\{Google\Chrome,Microsoft\Edge,BraveSoftware\Brave-Browser,…}\NativeMessagingHosts\com.bdm.host`. Pipe name: `bdm-ipc-<username>`.

- [ ] **Step 1: Failing tests.** `Frame_RoundTrips_UnicodeAndLargeMessage` (1 MB); `Read_ReturnsNullOnEof`; `Host_Add_ForwardsToPipeAndRepliesOk` (fake pipe server); `Host_AppNotRunning_LaunchesAppThenForwards` (Review Focus 5; fake launcher records one launch); `Host_AppFailsToStart_RepliesOkFalse` (so the extension can fall back); `Registrar_WritesManifestWithAllowedOriginAndRegistryKeys` (uses a test registry hive path).
- [ ] **Step 2:** `dotnet test tests/BDM.Host.Tests`. FAIL. **Step 3:** Implement. **Step 4:** PASS. Commit.
- [ ] **Step 5:** Generate the extension `key` once (`openssl genrsa 2048 | openssl rsa -pubout -outform DER | base64 -w0`), record the derived ID in `extension/KEY_README.md`.

### Task 10: Extension core (network layer, takeover, popup)

**Files:** Create `extension/manifest.json`, `src/background.js`, `src/detect/classify.js`, `src/detect/registry.js`, `src/detect/network.js`, `src/takeover.js`, `src/host.js`, `popup/popup.html`, `popup/popup.js`, `options/options.html`; Test `extension/test/classify.test.mjs`, `registry.test.mjs`, `takeover.test.mjs`

**Interfaces:**
- Produces: `classify({url, contentType, firstBytes}) → "hls"|"dash"|"video"|"audio"|"subtitle"|null` (by content type, then magic bytes — `#EXTM3U`, `<MPD`, `ftyp`, `1A45DFA3`, `ID3`, `OggS` — then URL pattern; never extension alone); `createRegistry() → { add(tabId, item), list(tabId), clear(tabId) }` where `item = {id, kind, url, frameUrl, pageUrl, title, headers, size?, duration?, protected?}` de-duplicated by normalized URL (query-signature params ignored for dedupe only); `shouldTakeOver({url, mime, size, altKey, host}, settings) → boolean`; `sendToHost(msg) → Promise<{ok, error?}>` using `chrome.runtime.sendNativeMessage("com.bdm.host", msg)`; badge = `list(tabId).length`.
- Manifest permissions: `webRequest`, `downloads`, `nativeMessaging`, `storage`, `tabs`, `scripting`, `contextMenus`, `cookies`, `debugger` as **optional** permission; host permissions `<all_urls>`. `cookies` is used to attach the browser's own `Cookie` header for the item's URL when sending to the host (this is the "browser cookie import"; browser cookie databases are never read).

- [ ] **Step 1: Failing tests (`node --test extension/test`).** `classify` cases: `application/vnd.apple.mpegurl`→hls; extensionless URL with `video/mp4`→video; `text/plain` body starting `#EXTM3U`→hls; `application/octet-stream` + `ftyp` bytes→video; `image/png`→null; `.mp4` URL with `text/html`→null. `registry`: same URL twice→one item; two tabs isolated; `clear` empties. `takeover`: honors min size, excluded host, `altKey` skip, unlisted mime ignored.
- [ ] **Step 2:** `node --test extension/test`. Expected FAIL.
- [ ] **Step 3:** Implement modules; wire `webRequest.onHeadersReceived` (extraHeaders) → classify → ambiguous responses probed with `fetch(url,{headers:{Range:"bytes=0-15"}})`; `downloads.onDeterminingFilename` → `shouldTakeOver` → cancel browser download → `sendToHost`; if `sendToHost` fails, **do not cancel** (browser downloads normally) and show a notification (Review Focus 5). Context menu: Download with BDM / all links / all images and media.
- [ ] **Step 4:** Tests PASS. Add `takeover.test.mjs` case `HostFailure_DoesNotCancelBrowserDownload`. Commit.
- [ ] **Step 5 (manual):** load unpacked in Chrome, run `tools/register-host.ps1`, click a `.zip` link; BDM receives it. Expected: item appears in the app log.

### Task 11: Extension page, DOM, iframe and worker layers

**Files:** Create `extension/src/detect/page-hook.js` (MAIN world, `document_start`, `all_frames`, `match_about_blank`), `dom-scan.js`, `frames.js`; Test `page-hook.test.mjs` (runs the hook in `node:vm` with fake `fetch/XMLHttpRequest/MediaSource`)

**Interfaces:** Produces page-hook messages `window.postMessage({__bdm:1, type:"media", url, via:"fetch"|"xhr"|"mse"|"blob", headers, bodyPrefix})`; `dom-scan.scan(root) → items` (video, audio, source, track; open shadow roots; `MutationObserver` for late nodes); background correlates worker/service-worker requests to tabs via `details.tabId` + `initiator`, and marks items `protected:true` when `MediaKeys`/`requestMediaKeySystemAccess` is used on the element.

- [ ] **Step 1: Failing tests.** `fetch_wrapper_reports_m3u8_response_and_still_returns_original_response`; `xhr_wrapper_reports_mpd`; `mse_appendBuffer_links_blob_to_recent_segment_urls`; `createObjectURL_blob_with_MediaSource_not_reported_as_file`; `dom_scan_finds_source_inside_open_shadow_root`; `dom_scan_sees_video_added_after_load`; `floating_button_appears_over_playing_video_with_found_item_and_respects_off_toggle` (IDM-style "Download this video" button, `float-button.js`, option on by default); `cookie_header_attached_for_item_url_in_host_message`; `hook_never_throws_into_page` (wrapped fn errors swallowed); `drm_element_marked_protected`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement; hooks must preserve `toString`/prototype shape and not alter behavior. **Step 4:** PASS. Commit.

### Task 12: Debugger fallback and per-site opt-in

**Files:** Create `extension/src/detect/debugger-fallback.js`, `src/siteSettings.js`; extend `popup/popup.js`; Test `siteSettings.test.mjs`

**Interfaces:** Produces `siteSettings.get(host) → {debugger:boolean, takeover:boolean}` persisted in `chrome.storage.sync`; `debuggerFallback.attach(tabId)` / `detach(tabId)` using `Network.enable` and `Network.responseReceived` → classify → `registry.add`. Popup shows a toggle "Deep scan this site (shows Chrome debugging bar)", off by default; requests the optional `debugger` permission on first enable.

- [ ] **Step 1: Failing tests.** `defaults_are_off`; `enable_persists_per_host_not_per_origin_port`; `attach_only_for_enabled_hosts`; `detach_on_tab_close`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement. **Step 4:** PASS. Commit.

### Task 13: HLS parser

**Files:** Create `src/BDM.Core/Streams/HlsParser.cs`, `HlsModels.cs`; Test `HlsParserTests.cs` with fixtures in `tests/BDM.Core.Tests/Fixtures/hls/`

**Interfaces:** Produces `static HlsPlaylist HlsParser.Parse(string text, Uri baseUri)`; `HlsPlaylist(bool IsMaster, IReadOnlyList<HlsVariant> Variants, IReadOnlyList<HlsMedia> Media, IReadOnlyList<HlsSegment> Segments, HlsMap? Map, bool Ended, double TargetDuration)`; `HlsVariant(Uri Url, int Bandwidth, string? Resolution, string? Codecs, string? AudioGroup, string? SubtitleGroup)`; `HlsMedia(string Type, string GroupId, Uri? Url, string? Language, string? Name)`; `HlsSegment(Uri Url, double Duration, long Sequence, long? ByteRangeOffset, long? ByteRangeLength, HlsKey? Key)`; `HlsKey(string Method, Uri? Uri, byte[]? Iv)`; `HlsMap(Uri Url, long? ByteRangeOffset, long? ByteRangeLength)`.

- [ ] **Step 1: Failing tests.** `Master_ParsesVariantsAndAudioGroups`; `Media_RelativeAndQueryUrlsResolveAgainstBase` (keeps query strings); `Segments_ByteRangeWithoutOffsetContinuesPreviousRange`; `Key_Aes128_ExplicitIv_And_ImplicitIvFromSequence`; `Fmp4_ExtXMap_Parsed`; `Live_NoEndlist_EndedFalse`; `Key_MethodSampleAes_FlaggedUnsupported` (Method preserved); `Garbage_NotM3u8_Throws FormatException`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement. **Step 4:** PASS. Commit.

### Task 14: DASH parser

**Files:** Create `src/BDM.Core/Streams/DashParser.cs`, `DashModels.cs`; Test `DashParserTests.cs`, fixtures in `Fixtures/dash/`

**Interfaces:** Produces `static DashManifest DashParser.Parse(string xml, Uri baseUri)`; `DashManifest(bool IsDynamic, IReadOnlyList<DashPeriod> Periods)`; `DashPeriod(IReadOnlyList<DashRepresentation> Representations)`; `DashRepresentation(string Id, string MimeType, string? Codecs, int Bandwidth, int? Width, int? Height, string? Language, Uri? InitUrl, IReadOnlyList<Uri> SegmentUrls, double DurationSeconds, bool Protected)`; supports SegmentTemplate (`$Number$`, `$Time$`, `$RepresentationID$`, `$Bandwidth$`), SegmentTimeline, SegmentList, SegmentBase/BaseURL, multi-period; `ContentProtection` with Widevine/PlayReady scheme → `Protected=true`.

- [ ] **Step 1: Failing tests.** `Template_Number_ExpandsAllSegments`; `Timeline_RepeatCountExpands`; `SegmentList_And_SingleBaseUrl_Mp4`; `MultiPeriod_ConcatenatesInOrder`; `BaseUrl_NestedResolution`; `ContentProtection_Widevine_MarksProtected`; `Dynamic_Mpd_IsDynamicTrue`; `Instagram_style_separate_video_and_audio_adaptation_sets_listed_separately`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement (System.Xml.Linq). **Step 4:** PASS. Commit.

### Task 15: Stream downloader, merge, live capture, subtitles

**Files:** Create `src/BDM.Core/Streams/StreamDownloader.cs`, `FfmpegLocator.cs`, `FfmpegMerger.cs`, `AesSegmentDecryptor.cs`, `StreamSource.cs`; Test `StreamDownloaderTests.cs` (test server serving generated tiny TS/fMP4 via ffmpeg `testsrc`)

**Interfaces:**
- Consumes: Tasks 4, 13, 14.
- Produces: `enum StreamKind { Hls, Dash }`; `record StreamSource(Uri ManifestUrl, StreamKind Kind, IReadOnlyDictionary<string,string> Headers, string? CookieHeader, string? Title, string? ManifestBody)`; `record StreamTrack(string Id, Uri Playlist)`; `record StreamChoice(string Label, StreamTrack Video, StreamTrack? Audio, IReadOnlyList<StreamTrack> Subtitles)`; `class FfmpegLocator { static string? Find(string? configured) }` (configured path → app dir → PATH); `class StreamDownloader { StreamDownloader(SegmentedDownloader d, FfmpegLocator ff); Task<IReadOnlyList<StreamChoice>> ListChoicesAsync(StreamSource s, CancellationToken ct); Task<string> DownloadAsync(StreamSource s, StreamChoice c, string destDir, IProgress<DownloadProgress>? p, CancellationToken ct); }`; throws `DownloadException`-like `ProtectedContentException` when `Protected`/`SAMPLE-AES`.

- [ ] **Step 1: Failing tests.** `Hls_Clear_ProducesPlayableMp4` (ffprobe duration ≈ source ±0.5 s); `Hls_Aes128_DecryptsAndMerges`; `Hls_Fmp4_WithMap_Merges`; `Dash_SeparateVideoAudio_PairedAndMuxed` (Instagram-style, audio+video present in output); `Live_NoEndlist_StopsOnCancelAndKeepsPlayableFile` (Review Focus 4); `Live_EndlistAppears_StopsAutomatically`; `Protected_Throws_ProtectedContentException`; `Subtitles_WebVtt_SavedNextToVideo_And_MergedWhenRequested`; `Thumbnail_SavedWhenPresent`; `Ffmpeg_Missing_ThrowsActionableError`; `Headers_AndCookie_SentOnEverySegmentRequest` (test server asserts).
- [ ] **Step 2:** FAIL. **Step 3:** Implement (segment downloads reuse `SegmentedDownloader` with `Connections=1` per segment and N segments in parallel; merge via `ffmpeg -f concat … -c copy`; AES-128 CBC per segment). **Step 4:** PASS. Commit.
- [ ] **Step 5:** Wire `addStream` in the app-side pipe server (Task 16) to enqueue stream jobs through `DownloadManager`.

### Task 16: WPF shell

**Files:** Create `src/BDM.App/BDM.App.csproj`, `App.xaml(.cs)`, `MainWindow.xaml`, `ViewModels/MainViewModel.cs`, `ViewModels/DownloadItemViewModel.cs`, `Views/AddUrlDialog.xaml`, `Views/ProgressDialog.xaml`, `Views/CompletionDialog.xaml`, `TrayHost.cs`, `ClipboardMonitor.cs`, `PipeServer.cs`, `Themes/Theme.xaml`; Test `tests/BDM.App.Tests/MainViewModelTests.cs` (view models only; no UI automation)

**Interfaces:** Consumes Tasks 5–9, 15. Produces `MainViewModel { ObservableCollection<DownloadItemViewModel> Items; ICommand AddUrl, Pause, Resume, Remove, OpenFile, OpenFolder; string SelectedCategory; string SearchText }`; `PipeServer.Start(DownloadManager m)` handling `HostMessage` (`add`, `addStream` → choice dialog or best-quality default per setting, `ping`); single-instance via named mutex that forwards a second launch's args to the first.

- [ ] **Step 1: Failing tests.** `Category_Filter_ShowsOnlyMatching`; `Search_FiltersByNameAndUrl`; `AddUrl_CreatesItemInMainQueue`; `Clipboard_UrlOfConfiguredType_RaisesPrompt`; `PipeAdd_CreatesItem_AndRepliesOk`; `SecondInstance_ForwardsToFirst`.
- [ ] **Step 2:** `dotnet test tests/BDM.App.Tests`. FAIL. **Step 3:** Implement the shell, IDM-style layout (category tree left, list center, toolbar top), per-download dialog with per-segment bars, tray with minimize-to-tray, drag-drop target window. Theme via owner's `theme-studio` tokens if available (the skill emits WPF ResourceDictionaries), else system theme. **Step 4:** PASS. Commit.
- [ ] **Step 5 (manual checklist):** launch app; add a URL; pause/resume; tray restore; drag a link onto the drop target. Expected: all work, no unhandled exceptions in the log.

### Task 17: WPF settings, scheduling, batch, grabber, logins, VPN profiles, history

**Files:** Create `Views/OptionsWindow.xaml`, `SchedulerWindow.xaml`, `BatchWindow.xaml`, `GrabberWindow.xaml`, `SitesLoginWindow.xaml`, `ProxyProfilesWindow.xaml`, `HistoryWindow.xaml`, matching view models in `ViewModels/`; Test `OptionsViewModelTests.cs`, `ProxyProfilesViewModelTests.cs`, `SitesLoginViewModelTests.cs`

**Interfaces:** Produces `OptionsViewModel` (connections 1–32, default folders per category, file-type list for takeover, proxy global default, ffmpeg/yt-dlp paths, speed limit, antivirus command, start-with-Windows); `ProxyProfilesViewModel { Add, Remove, ImportSurfshark(string text), SetPassword }` (password only goes to `CredentialVault`); `SitesLoginViewModel` (host → username + vault key, applied as Basic auth); per-download/queue/category "Route through…" picker using `ProxyRouting`.

- [ ] **Step 1: Failing tests.** `Connections_ClampedTo1To32`; `ImportSurfshark_AddsProfiles_NoPasswordInDatabase`; `SiteLogin_AppliedToMatchingHostOnly`; `Settings_ExportImport_RoundTrip`; `Antivirus_CommandRunsOnCompletionWithPathArgument`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement. **Step 4:** PASS. Commit.
- [ ] **Step 5 (manual):** import a sample Surfshark list, route one download through it against the local fake proxy; Expected: the proxy log shows the request.

### Task 18: yt-dlp integration, REST API and `bdm` CLI

**Files:** Create `src/BDM.Core/Ext/YtDlpRunner.cs`, `src/BDM.App/RestApi.cs`, `src/BDM.Cli/BDM.Cli.csproj`, `Program.cs`; Test `YtDlpRunnerTests.cs`, `RestApiTests.cs`

**Interfaces:** Produces `class YtDlpRunner { YtDlpRunner(string exePath); Task<IReadOnlyList<StreamChoice>> ListAsync(Uri pageUrl, string? cookieFile, CancellationToken ct); Task<string> DownloadAsync(Uri pageUrl, string formatId, string destDir, IProgress<DownloadProgress>? p, CancellationToken ct); }` (parses `yt-dlp -J` and `--progress-template`); REST on `127.0.0.1:<port>` with header `X-BDM-Token`: `POST /add {url, headers?}` → `202 {id}`, `GET /downloads`, `GET /downloads/{id}`, missing/wrong token → 401; `bdm add <url>`, `bdm list`, `bdm pause <id>` reading port+token from `%AppData%\BetterDownloadManager\api.json`.

- [ ] **Step 1: Failing tests.** `YtDlp_ParsesFormatsFromFixtureJson`; `YtDlp_MissingExe_ActionableError`; `Rest_NoToken_401`; `Rest_BindsLoopbackOnly` (listener endpoint is `127.0.0.1`); `Rest_Add_ReturnsId_AndQueuesDownload`; `Cli_Add_PostsToApi` (against test server).
- [ ] **Step 2:** FAIL. **Step 3:** Implement; app falls back to yt-dlp when a captured page URL is not a plain stream and the owner has set the yt-dlp path. **Step 4:** PASS. Commit.

### Task 19: Acceptance corpus (stream catcher gate)

**Files:** Create `tests/BDM.Corpus/` (Kestrel site + generated media), `tests/e2e/package.json`, `tests/e2e/catcher.spec.mjs` (Playwright, Chromium launched with `--load-extension=extension`), `docs/corpus-sites.md` (owner's real-site checklist)

**Interfaces:** Corpus pages, each expected to yield ≥1 item in the registry (read via a debug message to the background page): `hls-clear`, `hls-aes128`, `dash-vod`, `mse-blob` (page JS fetches fMP4 and appends to MediaSource), `iframe-embed`, `service-worker-fetch`, `extensionless-url` (`/v/12345`, `video/mp4`), `ig-story-like` (separate DASH video+audio fetched by page JS, requires cookie `sessionid` else 403, signed URL with `exp`), `drm-widevine` (expected `protected:true`, **not** downloadable).

- [ ] **Step 1: Failing tests.** One Playwright test per corpus page asserting the expected item kind and, for downloadable pages, a full end-to-end: item → host → app (headless harness) → output file plays (`ffprobe`). Plus `ig_story_like_download_succeeds_with_captured_cookie_and_fails_without`.
- [ ] **Step 2:** `cd tests/e2e && npm test`. Expected: FAIL. **Step 3:** Fix detectors (Tasks 10–12, 15) until all pass; any case that needs the debugger layer must pass with deep-scan enabled and be listed in `docs/corpus-sites.md`. **Step 4:** All PASS. Commit.
- [ ] **Step 5 (owner-supplied):** add each real site the owner names (starting with Instagram stories, logged in) to `docs/corpus-sites.md` with a manual pass/fail column; do not claim a site is supported until it passes there.

### Task 20: Installer and release polish

**Files:** Create `installer/bdm.iss` (Inno Setup, per-user), `tools/build-release.ps1`, `README.md`

**Interfaces:** Produces `dist/BDM-Setup.exe` that installs app + host + CLI to `%LocalAppData%\Programs\BetterDownloadManager`, registers the native host for Chrome/Edge/Brave (calls `HostRegistrar`), adds optional Start-with-Windows, bundles ffmpeg if the owner supplies it in `installer/vendor/`, and uninstalls cleanly (removes registry keys, leaves downloads and the database unless the user opts in).

- [ ] **Step 1:** `tools/build-release.ps1` runs `dotnet test` for all projects, `node --test extension/test`, then `dotnet publish -c Release -r win-x64` for App, Host, Cli, then compiles the installer. Expected: exits 0 only if all tests pass.
- [ ] **Step 2 (manual, clean user profile):** install, load the extension, click a download link and a stream from the corpus, uninstall. Expected: both captured; `HKCU` keys gone after uninstall.
- [ ] **Step 3:** Commit and tag `v0.1.0`.
