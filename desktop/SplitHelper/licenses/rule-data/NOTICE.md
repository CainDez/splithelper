# Bundled rule databases

SplitHelper redistributes unmodified `geosite.dat` and `Country.mmdb` from
MetaCubeX/meta-rules-dat release commit
`38711db8318645f994b3b0e2ab4332e87bbfb02c` (2026-11-10 12:12).
The exact file hashes and upstream URLs are in `components/main-core/manifest.json`.
The databases are separate from the Mihomo program and retain their own notices.

- Release data: https://github.com/MetaCubeX/meta-rules-dat/tree/38711db8318645f994b3b0e2ab4332e87bbfb02c
- Build source and acknowledgments: https://github.com/MetaCubeX/meta-rules-dat/tree/4178770badecb1b349fbcd62c737e0d7a2079729
- MetaCubeX build project: GNU GPL v3. The corresponding build-source archive is included in this directory.
- Country data: https://github.com/Loyalsoldier/geoip, published under CC BY-SA 4.0 **and** GNU GPL v3. Its README and both license texts are included.
- This product includes GeoLite2 data created by MaxMind, available from https://www.maxmind.com/.
- The upstream country database replaces mainland China ranges with https://github.com/gaoyifan/china-operator-ip (MIT). SplitHelper does not modify the database.

The bundled upstream READMEs preserve the data sources and acknowledgments.
License texts for domain-list-community, domain-list-custom, ios_rule_script,
dnsmasq-china-list, gfwlist, WindowsSpyBlocker and TrackersListCollection are
included alongside this notice. Upstream build references also include
https://github.com/xishang0128/rules. Each source retains its own license;
the Mihomo GPL notice does not replace these data notices.

`sources.json` records the source URL and SHA-256 of each included notice, as
retrieved on 2026-11-11. Copyright notices are preserved in those files.
