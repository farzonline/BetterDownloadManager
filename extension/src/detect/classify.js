// Decides what kind of media a response is. Content type first, then magic bytes, then (only for
// ambiguous types) a manifest extension. A media extension alone never counts: an error page named .mp4 is HTML.

const HLS_TYPES = new Set([
  'application/vnd.apple.mpegurl', 'application/x-mpegurl', 'application/mpegurl', 'audio/mpegurl', 'audio/x-mpegurl',
]);
const REJECT = /^(image\/|text\/html|text\/css|application\/json|application\/(x-)?javascript|text\/javascript|application\/xhtml|font\/|application\/wasm)/;
const AMBIGUOUS = new Set([
  '', 'application/octet-stream', 'binary/octet-stream', 'text/plain', 'text/xml', 'application/xml',
  'application/x-unknown', 'application/unknown',
]);
const SEGMENT_PATH = /\.(ts|m4s|cmfv|cmfa|m4f|aac)$/i;

export function isAmbiguous(contentType) {
  return AMBIGUOUS.has(normalize(contentType));
}

function normalize(contentType) {
  return (contentType || '').split(';')[0].trim().toLowerCase();
}

function pathOf(url) {
  try { return new URL(url).pathname; } catch { return url.split(/[?#]/)[0]; }
}

function toBytes(firstBytes) {
  if (!firstBytes) return null;
  return typeof firstBytes === 'string' ? new TextEncoder().encode(firstBytes) : firstBytes;
}

const ascii = (b, from, to) => String.fromCharCode(...b.slice(from, to));

function sniff(bytes) {
  const b = toBytes(bytes);
  if (!b || b.length < 3) return null;
  const head = ascii(b, 0, Math.min(b.length, 256)).replace(/^﻿|^\s+/, '');
  if (head.startsWith('#EXTM3U')) return 'hls';
  if (head.startsWith('WEBVTT')) return 'subtitle';
  if (head.startsWith('<') && head.includes('<MPD')) return 'dash';
  if (b.length >= 8 && ascii(b, 4, 8) === 'ftyp') return 'video';
  if (b[0] === 0x1a && b[1] === 0x45 && b[2] === 0xdf && b[3] === 0xa3) return 'video'; // matroska / webm
  if (head.startsWith('FLV')) return 'video';
  if (b.length >= 12 && ascii(b, 0, 4) === 'RIFF' && ascii(b, 8, 12) === 'AVI ') return 'video';
  if (head.startsWith('ID3') || head.startsWith('OggS') || head.startsWith('fLaC')) return 'audio';
  if (b[0] === 0xff && (b[1] & 0xe0) === 0xe0) return 'audio'; // MPEG audio / ADTS frame sync
  return null;
}

/** @returns {'hls'|'dash'|'video'|'audio'|'subtitle'|'segment'|null} */
export function classify({ url, contentType = '', firstBytes } = {}) {
  const ct = normalize(contentType);
  if (REJECT.test(ct)) return null;
  if (HLS_TYPES.has(ct)) return 'hls';
  if (ct === 'application/dash+xml') return 'dash';
  const segmentLike = SEGMENT_PATH.test(pathOf(url));
  if (ct === 'video/mp2t' || ct === 'video/iso.segment') return 'segment';
  if (ct.startsWith('video/')) return segmentLike ? 'segment' : 'video';
  if (ct.startsWith('audio/')) return segmentLike ? 'segment' : 'audio';
  if (ct === 'text/vtt' || ct === 'application/x-subrip') return 'subtitle';

  const sniffed = sniff(firstBytes);
  if (sniffed) return sniffed === 'video' && segmentLike ? 'segment' : sniffed;
  if (AMBIGUOUS.has(ct)) {
    const p = pathOf(url);
    if (/\.m3u8$/i.test(p)) return 'hls';
    if (/\.mpd$/i.test(p)) return 'dash';
  }
  return null;
}
