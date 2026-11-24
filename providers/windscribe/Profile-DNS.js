// Bind only to this Windscribe profile. Keep Clash TUN off in application settings.
function main(config) {
  config.dns = {
  "enable": true,
  "listen": "127.0.0.1:1053",
  "ipv6": false,
  "enhanced-mode": "redir-host",
  "respect-rules": false,
  "nameserver": [
    "https://1.1.1.1/dns-query#Windscribe"
  ],
  "nameserver-policy": {
    "geosite:cn": [
      "https://223.5.5.5/dns-query#ChinaDirect"
    ]
  }
};
  delete config["interface-name"];
  return config;
}
