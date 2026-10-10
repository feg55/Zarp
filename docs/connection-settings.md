# Connection settings

Open **Settings** to change them. Settings are saved locally and apply on the next connection. Changing the server or the WARP endpoints clears the previous test results, because they described another connection.

| Setting | WARP (official client) | Your own server |
|---|---|---|
| Country filter for strategies | yes | not used, there is a single server |
| Custom WARP endpoints | yes | not used |
| Routing presets, GeoIP and GeoSite | no | yes |
| Per-app proxy | no | yes |

The official WARP client has no per-app mode and cannot route by country or domain, and Zarp does not replace it. These two features need a tunnel that Zarp controls, so they work with **your own server**, which Zarp connects through sing-box and its own network adapter. The windows say so when WARP is the active mode.

## Server and endpoints

Open **Settings → Server and endpoints**.

**Custom WARP endpoints.** Enter one numeric `IP:port` or `[IPv6]:port` per line. Blank lines and `#` comments are accepted. An empty list restores the default WARP addresses. The addresses must be compatible WARP endpoints, not arbitrary proxies: the field cannot turn another server into WARP. A scan rotates through your list, rechecks included, and the connection uses the address on which the strategy passed its last check. With a single address a recheck uses a fresh connection but cannot isolate the endpoint, and the result says so (works, 2 checks, not independent). While a strategy is active, winws2 also intercepts your addresses, even outside the Cloudflare ranges.

**Use my server instead of WARP.** Paste one link:

| Protocol | Supported parameters |
|---|---|
| VLESS | UUID, TCP, WebSocket, gRPC, HTTPUpgrade, TLS, Reality (`pbk`, `sid`), Vision (`flow=xtls-rprx-vision`), uTLS fingerprint |
| Trojan | password, TLS, TCP, WebSocket, gRPC, HTTPUpgrade |
| Hysteria2 / hy2 | password, TLS, SNI, ALPN, Salamander obfuscation |

Common parameters are `sni`, `peer`, `alpn`, `insecure` and `allowInsecure`. Transport fields are `type`, `path`, `host` and `serviceName`. Unsupported transports and parameters are rejected, not silently dropped. One link only: no subscriptions, XHTTP, port hopping or arbitrary JSON. Certificate verification stays on unless the link turns it off. The password and the UUID never appear in the strategy name, the log or an error message, and the saved link stays in `zarp.json` in your data folder.

The server replaces WARP: Zarp does not register a WARP device, does not need the WARP client and does not start zapret2. The check requires a valid HTTPS answer from `cdn-cgi/trace` that came through the server, and the connection time is the time of the first answer. If the server has no IPv6 access, turn off **IPv6 through the server** in the same window. DNS requests go to the DNS server from that window (1.1.1.1 by default) through your server.

The engine is [sing-box 1.13.14](https://github.com/SagerNet/sing-box/tree/v1.13.14), GPL-3.0-or-later, embedded into `Zarp.exe` and started as a separate process. See [third-party notices](../THIRD_PARTY_NOTICES.md).

## Routing

Open **Settings → Routing**, switch the rules on and choose one preset. The built-in presets (local networks, Russia, Iran, China) send local addresses and the addresses of the country directly, and everything else goes through the server. **Edit** opens the three groups **Direct**, **Proxy** and **Block**, one rule per line. You can edit the built-in presets and reset them, and create and delete your own. Priority is **Block, then Proxy, then Direct**. Traffic that no rule matches goes through the server, with one exception made for a desktop: local network addresses stay direct unless a rule says otherwise, so the router, printers and file shares keep working.

| Rule | Matches |
|---|---|
| `geoip:ru`, `geoip:private` | GeoIP category (`private` is built in) |
| `geosite:youtube` | GeoSite category |
| `geosite:google@cn`, `geosite:google@!cn` | entries with the attribute, or without it |
| `domain:example.com` or `example.com` | the domain and its subdomains |
| `full:example.com` | exactly this domain |
| `keyword:example` | domains that contain the text |
| `regexp:pattern` | RE2 expression for the domain |
| `1.2.3.4`, `2001:db8::/32` | IPv4 or IPv6 address or range |

Blank lines and lines that start with `#` are ignored. Use punycode for international domain names. Syntax is checked when you save, with the real line number. Unknown categories, missing databases and expressions that sing-box rejects stop the connection with an explanation: a rule is never skipped silently.

**GeoIP and GeoSite** opens the download settings. Each database has an editable HTTPS address and a Download or Update button. The defaults are the V2Ray/Xray `.dat` files of [Loyalsoldier/v2ray-rules-dat](https://github.com/Loyalsoldier/v2ray-rules-dat). Formats `.db`, `.srs` and MMDB are not accepted. Downloads are explicit, up to 64 MiB per file, with a three minute limit and HTTPS-only redirects. Every record is validated before the file replaces the old one, so a failed or cancelled update keeps the last good database. A different address has a separate stored copy. Local ranges need no download. For the default source Zarp also carries the ranges of Russia, Iran and China from [ipverse/country-ip-blocks](https://github.com/ipverse/country-ip-blocks) (CC0), used until you download your own GeoIP. GeoSite always needs a download.

## Per-app proxy

Open **Settings → Per-app proxy**, switch **Proxy selected programs only** on and choose programs. The list shows running programs, Start menu shortcuts and registered application paths. Search matches the name and the path, and **Add program** takes any `.exe`. Only the chosen programs use the server (and the routing rules), everything else connects as usual. A program is recognized by the path of its `.exe`, ignoring case. Browsers and launchers often start helper programs of their own, so choose the one that really makes the connection. Zarp itself always goes through the server while the mode is on, because that is how it checks the adapter.

If the mode is on and none of the chosen programs exists any more, the connection is refused instead of falling back to all programs.

## Strategy countries

The filter above the strategy list takes several countries at once: the visible list, quick and full scans, the power button, rechecks and the use of saved results all see the union of the chosen countries. **All** clears the filter. While a country is chosen, strategies without desync (plain WARP) and strategies without a country tag are left out. Changing the filter does not delete saved results. Country tags are suggestions for what to test, not proof that a strategy works with every provider in that country. Generic Google QUIC fakes and TLS splits are offered for all three countries, strategies built around VK or Gosuslugi fakes only for Russia. The strategy catalog is in [strategies.md](strategies.md).

A custom strategy may have an optional fourth column:

```text
My TLS | h2 | --payload=tls_client_hello --lua-desync=multisplit:pos=1,sld | ru,ir,cn
```

Lines with three columns stay valid and appear under **All**. Adding or removing the country column keeps the strategy identity, so its saved results stay.

The country filter and the routing presets are independent.

## How your own server is connected

Zarp checks the server with a local sing-box that has no adapter: a real HTTPS request goes through it twice, so an unreachable server never takes your traffic. Then sing-box starts again with a network adapter named **Zarp** that routes the whole system through it (`auto_route`, `strict_route`, gVisor stack). The adapter uses the Wintun driver, which is carried inside sing-box and installed the first time an adapter is created (that needs administrator rights, which Zarp always has). The driver stays installed after the adapter is gone, as it does for other programs that use it. The server name is resolved first by the system DNS, while the network is still untouched. After the adapter is up, Zarp sends a request of its own through it and removes the adapter at once if nothing passes. DNS requests from every program are answered by sing-box and sent through the server, with reverse mapping so that domain rules also work for connections that only show an IP. Streams that carry a domain (TLS, HTTP, QUIC) are sniffed for domain rules.

The configuration, which holds the password, is written for sing-box to read and deleted right after it starts. The local test port accepts only random credentials. `sing-box.exe` is started only if it is byte for byte the file embedded in `Zarp.exe`, and the file is locked against replacement while it starts. Disconnecting, exiting (with **Disconnect on exit**) and a Windows shutdown stop sing-box, and Windows then removes the adapter and its routes. A sing-box left behind by a crash is picked up on the next start, or stopped if it cannot be recognized.

## Limitations

- IP lists describe address allocations, not guaranteed physical locations.
- Programs with their own encrypted DNS or Encrypted Client Hello can hide a domain from domain rules. IP rules still apply.
- Another VPN with its own adapter conflicts with this one. Zarp warns about it, as it does for WARP.
- Your own server mode, routing and per-app proxy are not available in the WARP mode (see the table above).
- Subscriptions, arbitrary JSON, XHTTP and Hysteria2 port hopping are not supported.
- No strategy can promise a permanent DPI bypass, and candidates need testing on each provider.

## Verification

The regression tests [parse every kind of link](testing.md), start real VLESS (TCP, WebSocket, HTTPUpgrade), Trojan (TCP, gRPC) and Hysteria2 (with Salamander) servers on localhost and pass TCP and UDP traffic through each of them. They run the connection check of Zarp through a real sing-box probe port (its login and password, plain HTTP and `CONNECT`) and check that a changed `sing-box.exe` is refused. They check the routing rules with the real sing-box (priority, every kind of rule, GeoIP and GeoSite databases, per-app selection by process path), validate `.dat` files, the download rules and every window in every language. They never start the network adapter, WARP or WinDivert.

What the tests cannot cover is the adapter itself on your network: routes, DNS and the firewall are Windows and sing-box territory. That is why the adapter check and the cleanup described above exist.
