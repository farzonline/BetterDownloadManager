import test from 'node:test';
import assert from 'node:assert/strict';
import { classify } from '../src/detect/classify.js';

const enc = (s) => new TextEncoder().encode(s);
const bytes = (...b) => new Uint8Array(b);

test('apple mpegurl content type is hls', () => {
  assert.equal(classify({ url: 'https://x/a', contentType: 'application/vnd.apple.mpegurl' }), 'hls');
  assert.equal(classify({ url: 'https://x/a', contentType: 'application/x-mpegURL; charset=utf-8' }), 'hls');
});

test('dash content type is dash', () => {
  assert.equal(classify({ url: 'https://x/a', contentType: 'application/dash+xml' }), 'dash');
});

test('extensionless url with video/mp4 is video', () => {
  assert.equal(classify({ url: 'https://x/v/12345?id=9', contentType: 'video/mp4' }), 'video');
});

test('text/plain body starting #EXTM3U is hls', () => {
  assert.equal(classify({ url: 'https://x/play', contentType: 'text/plain', firstBytes: enc('#EXTM3U\n#EXT-X-VERSION:3') }), 'hls');
});

test('xml body containing <MPD is dash', () => {
  const body = '<?xml version="1.0" encoding="UTF-8"?>\n<MPD xmlns="urn:mpeg:dash:schema:mpd:2011">';
  assert.equal(classify({ url: 'https://x/m', contentType: 'application/xml', firstBytes: enc(body) }), 'dash');
});

test('octet-stream with ftyp bytes is video', () => {
  const b = bytes(0, 0, 0, 0x20, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6f, 0x6d);
  assert.equal(classify({ url: 'https://x/blob', contentType: 'application/octet-stream', firstBytes: b }), 'video');
});

test('octet-stream with matroska/webm magic is video', () => {
  assert.equal(classify({ url: 'https://x/b', contentType: 'application/octet-stream', firstBytes: bytes(0x1a, 0x45, 0xdf, 0xa3) }), 'video');
});

test('ID3 and OggS magic are audio', () => {
  assert.equal(classify({ url: 'https://x/a', contentType: 'application/octet-stream', firstBytes: enc('ID3\x03') }), 'audio');
  assert.equal(classify({ url: 'https://x/a', contentType: 'application/octet-stream', firstBytes: enc('OggS\x00') }), 'audio');
});

test('audio/* and text/vtt', () => {
  assert.equal(classify({ url: 'https://x/a', contentType: 'audio/mpeg' }), 'audio');
  assert.equal(classify({ url: 'https://x/s', contentType: 'text/vtt' }), 'subtitle');
  assert.equal(classify({ url: 'https://x/s', contentType: 'text/plain', firstBytes: enc('WEBVTT\n\n') }), 'subtitle');
});

test('images and other non-media are null', () => {
  assert.equal(classify({ url: 'https://x/a.png', contentType: 'image/png' }), null);
  assert.equal(classify({ url: 'https://x/api', contentType: 'application/json' }), null);
});

test('mp4 url with text/html (an error page) is null — never by extension alone', () => {
  assert.equal(classify({ url: 'https://x/video.mp4', contentType: 'text/html; charset=utf-8' }), null);
});

test('hls/dash segments are classed as segment, not video', () => {
  assert.equal(classify({ url: 'https://x/seg1.ts', contentType: 'video/mp2t' }), 'segment');
  assert.equal(classify({ url: 'https://x/chunk-1.m4s', contentType: 'video/mp4' }), 'segment');
  assert.equal(classify({ url: 'https://x/seg.aac?x=1', contentType: 'audio/aac' }), 'segment');
});

test('last resort: manifest extension when type is ambiguous and no bytes are available', () => {
  assert.equal(classify({ url: 'https://x/master.m3u8?t=1', contentType: 'application/octet-stream' }), 'hls');
  assert.equal(classify({ url: 'https://x/manifest.mpd', contentType: '' }), 'dash');
  assert.equal(classify({ url: 'https://x/unknown', contentType: 'application/octet-stream' }), null);
});
