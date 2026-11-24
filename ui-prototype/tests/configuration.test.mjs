import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { parse } from "yaml";
import { createProfile, formErrors } from "../src/configuration.mjs";

const read = (path) => readFileSync(new URL(path, import.meta.url), "utf8");
const values = {
  ip: "10.23.45.1",
  port: "61053",
  mixedPort: "7899",
  username: "gateway: # user",
  password: 'a:" # b\nnew-line\\tail',
};

for (const provider of ["Mullvad", "Windscribe"]) {
  test(`${provider}: downloaded profile preserves reviewed routing, DNS and helper configuration`, () => {
    const template = read(`../src/templates/${provider.toLowerCase()}.yaml`);
    const original = parse(
      read(
        `../../providers/${provider.toLowerCase()}/Clash-${provider}${provider === "Windscribe" ? ".example" : ""}.yaml`,
      ),
    );
    const generated = parse(createProfile(template, provider, values));
    assert.deepEqual(generated.rules, original.rules);
    assert.deepEqual(generated.dns, original.dns);
    assert.deepEqual(generated["proxy-groups"], original["proxy-groups"]);
    assert.deepEqual(
      generated.proxies.find((proxy) => proxy.name === "ChinaDirect"),
      original.proxies.find((proxy) => proxy.name === "ChinaDirect"),
    );
    assert.deepEqual(generated.tun, { enable: false });
    assert.equal(generated.ipv6, false);
    assert.equal(generated["allow-lan"], false);
    assert.equal(generated["mixed-port"], 7899);
    if (provider === "Windscribe") {
      assert.equal(generated.rules[0], "PROCESS-NAME,Windscribe.exe,DIRECT");
      assert.equal(generated["find-process-mode"], "strict");
      assert.equal(generated.proxies[0].username, values.username);
      assert.equal(generated.proxies[0].password, values.password);
    } else {
      assert.deepEqual(generated.proxies[0], original.proxies[0]);
    }
  });
  test(`${provider}: downloadable DNS extension is identical to its provider source`, () => {
    assert.equal(
      read(`../src/templates/${provider.toLowerCase()}-dns.js`),
      read(`../../providers/${provider.toLowerCase()}/Profile-DNS.js`),
    );
  });
}

test("Windscribe preview redacts both credentials while download preserves special characters", () => {
  const source = read("../src/templates/windscribe.yaml");
  const preview = createProfile(source, "Windscribe", values, true);
  const config = parse(preview);
  assert.equal(config.proxies[0].username, "[已隐藏]");
  assert.equal(config.proxies[0].password, "[已隐藏]");
  assert.equal(preview.includes(values.username), false);
  assert.equal(preview.includes(values.password), false);
});

test("incomplete credentials, invalid ports and documentation addresses cannot produce a profile", () => {
  const template = read("../src/templates/windscribe.yaml");
  for (const change of [
    { password: "" },
    { username: " " },
    { port: "65536" },
    { mixedPort: "0" },
    { ip: "999.2.3.4" },
    { ip: "192.0.2.1" },
  ]) {
    const invalid = { ...values, ...change };
    assert.notEqual(Object.keys(formErrors("Windscribe", invalid)).length, 0);
    assert.throws(() => createProfile(template, "Windscribe", invalid));
  }
});
