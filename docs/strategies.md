# Built-in strategies

A strategy is a WARP tunnel protocol plus a [winws2](https://github.com/bol-van/zapret2) profile. Zarp tests them in the order below: the most promising ones come first, and HTTP/3 and HTTP/2 strategies alternate, so a quick scan reaches both kinds early. The names are the same in every language.

Fakes are real packets of services that are not blocked (google.com, vk.com, gosuslugi.ru), because DPI ignores empty ones.

## QUIC (MASQUE over HTTP/3)

The first packet of the WARP tunnel is a QUIC Initial. These strategies put something harmless in front of it, or hide it from DPI that does not reassemble IP fragments.

| Strategy | What it does |
|---|---|
| WARP QUIC: fake google ×6 | Sends 6 copies of a real google.com QUIC Initial before the real packet. |
| WARP QUIC: fake google ×3 | The same with 3 copies. |
| WARP QUIC: fake vk ×6 | The same with a vk.com Initial. |
| WARP QUIC: fake google ×11 | The same with 11 copies. |
| WARP QUIC: fakes google + vk | 3 copies of each. |
| WARP QUIC: fake google ttl=4 ×6 | Fakes with a short TTL: they expire on the way, so only DPI close to you sees them. |
| WARP QUIC: IP fragmentation | Cuts the first packet into two IP fragments after the 8-byte UDP header. |
| WARP QUIC: fake + IP fragmentation | 6 fakes, then the fragmented real packet. |

## TLS (MASQUE over HTTP/2)

The tunnel starts with a TLS ClientHello over TCP 443, which carries the server name in clear text.

| Strategy | What it does |
|---|---|
| WARP TLS: disorder on SNI points | Splits the ClientHello at several points around the server name and sends the pieces in reverse order. |
| WARP TLS: fake google md5 + split | A fake google.com ClientHello with a broken TCP MD5 option (the server ignores it), then a split. |
| WARP TLS: seqovl google | The first segment carries 681 bytes of a fake google.com ClientHello in front of the real data, overlapping earlier sequence numbers. |
| WARP TLS: disorder + seqovl | Reversed segments with a one byte overlap. |
| WARP TLS: fake vk badseq + disorder | A fake vk.com ClientHello with a wrong sequence number, then reversed segments. |
| WARP TLS: hostfakesplit vk.com | A fake host name (vk.com) around the real one. |
| WARP TLS: fake gosuslugi badack + split | A fake gosuslugi.ru ClientHello with a wrong acknowledgement number, then a split. |

## Where the syntax comes from

The parameters follow the zapret2 authors' own material for the release Zarp bundles (`blockcheck2.d/standard` scripts and the manual): the QUIC fake and IP fragmentation sets, the list of split markers `1,sniext+1,host+1,midsld-2,midsld,midsld+2,endhost-1`, the `seqovl` pairs and the TCP fooling variants (`tcp_md5`, `tcp_seq=-3000`, `tcp_ack=-66000` with `tcp_ts_up`). A few numbers (11 repeats, `seqovl=681`) are common in presets that circulate in the zapret community; treat them as starting points.

Removed from earlier versions:

- **WireGuard strategies.** WARP uses MASQUE by default, and the 148-byte WireGuard handshake has a fixed signature that is easy for DPI to match. You can still add your own with the `wg` transport in `strategies.txt`.
- **Close variants** of strategies that stay: `fake google ×10`, `fake vk ttl=4` and `fake google badsum`. Fewer variants mean a shorter search.
- **The "no zapret" control** (plain WARP). Zarp exists for networks where WARP does not connect by itself, so testing it only added seconds to every scan. A line with empty arguments in `strategies.txt` still gives you one if you want it.

## How they are checked

The tests do not touch WARP or the driver, but they do catch most mistakes before a release:

- every strategy is parsed by the real `winws2 --dry-run` from the embedded zapret2, with and without the WARP address restriction;
- a missing blob file is rejected, so a strategy cannot ship without its fake packet;
- function and argument names must exist in the Lua scripts of the bundled release, position markers must be valid, and a `multidisorder` overlap must be smaller than its first split position (zapret2 silently cancels it otherwise).

What the tests cannot tell is whether your DPI is fooled. The search on your own network does that, which is why every candidate has to pass twice.

## Your own strategies

Add them to `%LOCALAPPDATA%\Zarp\strategies.txt`, one per line:

```
# name | transport (h3, h2, wg) | winws2 profile args
My QUIC | h3 | --payload=quic_initial --lua-desync=fake:blob=quic_google:repeats=8
```

Available blobs: `quic_google`, `quic_vk`, `tls_google`, `tls_vk`, `tls_gosuslugi`, `stun_fake`, `zero64`, plus `fake_default_quic` and `fake_default_tls` built into zapret2. See the [zapret2 manual](https://github.com/bol-van/zapret2/blob/master/docs/manual.en.md) for the `--lua-desync` syntax.
