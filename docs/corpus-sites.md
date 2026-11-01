# Real-site checklist for the stream catcher

The automated corpus (`tests/e2e`) proves the detection layers work on every *shape* of stream I could build locally
(manifests via fetch/XHR, MediaSource blobs, iframes, service workers, extensionless URLs, signed cookie-gated
DASH, DRM). It cannot prove a *specific* site works: sites change, and some need your login.

**BDM does not claim a site is supported until it passes here.** Add every site where IDM misses a stream, then
fill in the result columns by hand.

## How to test a site

1. Load the extension (`extension/` folder) in Chrome/Edge/Brave: `chrome://extensions` → Developer mode → Load unpacked.
2. Register the host once: `bdm-host.exe --register depjhibopdmbilmmphmipdklabicghkf chrome edge brave`
   (the installer does this for you).
3. Open the page, **play the video**, then click the BDM toolbar button. Does the stream appear in the list?
4. Click *Download*. Does the file land in your downloads folder, and does it play with sound and picture?
5. If nothing appears: open the popup on that tab, switch on **Deep scan this site**, reload and play again
   (this is the debugger fallback layer and shows Chrome's "debugging this browser" bar).
6. If that fails too, note it in the table and send me the page address and what happened.

## Results

| Site / page type | Needs login? | Caught (popup lists it)? | Deep scan needed? | Downloads and plays? | Notes |
|---|---|---|---|---|---|
| Instagram story (logged in) | yes | _not yet tested_ | _?_ | _?_ | The first site on the list. Stories use short-lived signed URLs and separate video/audio tracks; the cookie from your own session is passed along. Download right after catching it, before the link expires. |
| _add a row for every site IDM misses_ | | | | | |

## What cannot be caught, by design

- **DRM-protected video** (Netflix, Disney+, most paid streaming, anything using Widevine/PlayReady/FairPlay): BDM
  flags it as "Protected (DRM)" and refuses it. It does not try to get around the protection.
- **Live DASH** and DASH with several periods are not supported yet; live **HLS** is (stop it from the app to finish
  the file).
- Sites that need an interactive login step inside the download itself (BDM reuses your browser's cookies, it never
  logs in for you).
