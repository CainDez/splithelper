const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

for (const [provider, group] of [['mullvad', 'Mullvad'], ['windscribe', 'Windscribe']]) {
  const source = fs.readFileSync(path.join(__dirname, '..', 'providers', provider, 'Profile-DNS.js'), 'utf8');
  const transform = vm.runInNewContext(source + '\nmain;', {}, { timeout: 1000 });
  const rules = ['DOMAIN-SUFFIX,example.com,ChinaDirect', `MATCH,${group}`];
  if (provider === 'windscribe') rules.unshift('PROCESS-NAME,Windscribe.exe,DIRECT');
  const proxies = [{ name: 'Official', type: 'socks5' }];
  const tun = { enable: false };
  const config = { dns: { nameserver: ['udp://legacy.invalid'], fallback: ['legacy.invalid'] },
    'interface-name': 'stale-interface', rules, proxies, tun };
  const result = transform(config);
  const dns = JSON.parse(JSON.stringify(result.dns));
  assert.deepEqual(dns.nameserver, [`https://1.1.1.1/dns-query#${group}`]);
  assert.deepEqual(dns['nameserver-policy']['geosite:cn'], ['https://223.5.5.5/dns-query#ChinaDirect']);
  assert.equal(dns.fallback, undefined);
  assert.equal(dns.ipv6, false);
  assert.equal(dns.listen, '127.0.0.1:1053');
  assert.equal(result['interface-name'], undefined);
  assert.equal(result.rules, rules);
  assert.equal(result.proxies, proxies);
  assert.equal(result.tun, tun);
  assert.equal(result.tun.enable, false);
  console.log(`${provider}: DNS override preserves rules, nodes and TUN state.`);
}
