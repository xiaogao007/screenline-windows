import assert from "node:assert/strict";
import { generateKeyPairSync, sign, webcrypto } from "node:crypto";
import { readFile } from "node:fs/promises";
import test from "node:test";

// Load the browser module without adding package metadata or dependencies to
// this static website. This works independently of a parent's package.json.
const source = await readFile(new URL("../release.js", import.meta.url), "utf8");
const { verifyReleaseEnvelope, releaseLinks, parseFlatObject, readBoundedResponse,
  fetchVerifiedRelease, initReleaseClient, validatePublicKey, RELEASE_BASE_URL } =
  await import(`data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);
const { publicKey, privateKey } = generateKeyPairSync("rsa", { modulusLength: 2048 });
const exported = publicKey.export({ format: "jwk" });
const key = { kty: exported.kty, n: exported.n, e: exported.e, alg: "RS256" };
const valid = {
  schema: 1, tag: "v1.2.3", installer: "Screenline-v1.2.3-windows-x64-setup.exe",
  size: 2303398, sha256: "a".repeat(64), notes: "新增国内下载。\n仍由用户确认安装。",
};
const encode = (value) => Buffer.from(JSON.stringify(value));
function signed(value = valid, raw) {
  const payload = raw || encode(value);
  return encode({ payload: payload.toString("base64"), signature: sign("sha256", payload, privateKey).toString("base64") });
}
const verify = (bytes) => verifyReleaseEnvelope(new Uint8Array(bytes), key, webcrypto);

test("the checked-in OSS snapshot verifies against the production public key", async () => {
  const bytes = await readFile(new URL("../releases/latest.json", import.meta.url));
  const productionKey = JSON.parse(await readFile(new URL("../release-key.json", import.meta.url), "utf8"));
  const manifest = await verifyReleaseEnvelope(new Uint8Array(bytes), productionKey, webcrypto);
  assert.match(manifest.tag, /^v\d+\.\d+\.\d+/);
  assert.ok(manifest.size > 0);
  assert.ok(Object.isFrozen(manifest));
});

test("a real RSA signature authenticates exact raw bytes", async () => {
  const raw = Buffer.from(`{ "notes":${JSON.stringify(valid.notes)}, "sha256":"${valid.sha256}","size":2303398,"installer":"${valid.installer}","tag":"v1.2.3","schema":1 }`);
  assert.deepEqual({ ...await verify(signed(valid, raw)) }, valid);
});

test("tampering payload or signature is rejected", async () => {
  const envelope = JSON.parse(signed());
  envelope.payload = encode({ ...valid, tag: "v9.9.9" }).toString("base64");
  await assert.rejects(verify(encode(envelope)), (error) => error.code === "signature");
  const wrongSignature = JSON.parse(signed());
  const signature = Buffer.from(wrongSignature.signature, "base64");
  signature[0] ^= 1;
  wrongSignature.signature = signature.toString("base64");
  await assert.rejects(verify(encode(wrongSignature)), (error) => error.code === "signature");
});

test("payload is not decoded before verifying its signature", async () => {
  const envelope = JSON.parse(signed());
  envelope.payload = Buffer.from([0xff, 0xff]).toString("base64");
  await assert.rejects(verify(encode(envelope)), (error) => error.code === "signature");
});

test("authenticated but unsupported schemas and fields are rejected", async () => {
  for (const payload of [{ ...valid, schema: 2 }, { ...valid, schema: "1" },
    { ...valid, url: "https://example.org/malware.exe" }, { ...valid, notes: undefined }]) {
    await assert.rejects(verify(signed(payload)));
  }
});

test("duplicate keys, nested data, invalid JSON and noncanonical Base64 are rejected", async () => {
  assert.throws(() => parseFlatObject('{"tag":"v1.2.3","tag":"v1.2.4"}'));
  assert.throws(() => parseFlatObject('{"nested":{"schema":1}}'));
  assert.throws(() => parseFlatObject('{"tag":"v1.2.3",}'));
  assert.throws(() => parseFlatObject('{"schema":01}'));
  const raw = Buffer.from(JSON.stringify(valid).replace('"schema":1', '"schema":1,"schema":1'));
  await assert.rejects(verify(signed(valid, raw)));
  const envelope = JSON.parse(signed());
  await assert.rejects(verify(Buffer.from(`{"payload":"${envelope.payload}","payload":"${envelope.payload}","signature":"${envelope.signature}"}`)));
  envelope.payload += "\n";
  await assert.rejects(verify(encode(envelope)));
});

test("stable tags and exact asset names prevent arbitrary download targets", async () => {
  for (const tag of ["v1.2.3-beta", "v01.2.3", "https://example.org/", "v1.2.3/../evil", "v1.2.3\n"]) {
    await assert.rejects(verify(signed({ ...valid, tag, installer: `Screenline-${tag}-windows-x64-setup.exe` })));
  }
  await assert.rejects(verify(signed({ ...valid, installer: "../../evil.exe" })));
  await assert.rejects(verify(signed({ ...valid, installer: "https://example.org/setup.exe" })));
  const manifest = await verify(signed());
  const links = releaseLinks(manifest);
  assert.equal(links.installer, RELEASE_BASE_URL + "releases/v1.2.3/Screenline-v1.2.3-windows-x64-setup.exe");
  assert.equal(links.portable, RELEASE_BASE_URL + "releases/v1.2.3/Screenline-v1.2.3-windows-x64.zip");
  assert.equal(links.source, RELEASE_BASE_URL + "releases/v1.2.3/Screenline-v1.2.3-source.zip");
  assert.equal(links.checksums, RELEASE_BASE_URL + "releases/v1.2.3/SHA256SUMS.txt");
});

test("installer size, digest and notes enforce application protocol limits", async () => {
  for (const size of [1, 256 * 1024 * 1024 + 1, 1.1, "42", true]) {
    await assert.rejects(verify(signed({ ...valid, size })));
  }
  for (const sha256 of ["A".repeat(64), "a".repeat(63), "../setup.exe"]) {
    await assert.rejects(verify(signed({ ...valid, sha256 })));
  }
  for (const notes of ["x".repeat(16385), "\u0000", "\u0085", "\ud800"]) {
    await assert.rejects(verify(signed({ ...valid, notes })));
  }
  await verify(signed({ ...valid, notes: "行一\r\n行二\t😀" }));
});

test("oversized envelopes and signed payloads are rejected", async () => {
  await assert.rejects(verify(Buffer.alloc(128 * 1024 + 1)));
  await assert.rejects(verify(signed(valid, Buffer.alloc(64 * 1024 + 1, 32))));
});

test("only a public RSA key with sufficient modulus size can verify", async () => {
  assert.throws(() => validatePublicKey({ ...key, d: "private" }));
  assert.throws(() => validatePublicKey({ ...key, n: Buffer.alloc(128, 255).toString("base64url") }));
  assert.throws(() => validatePublicKey({ ...key, e: "Aw" }));
  const other = generateKeyPairSync("rsa", { modulusLength: 2048 }).publicKey.export({ format: "jwk" });
  await assert.rejects(verifyReleaseEnvelope(new Uint8Array(signed()), { kty: other.kty, n: other.n, e: other.e, alg: "RS256" }, webcrypto));
});

test("response streaming enforces byte limits even without Content-Length", async () => {
  await assert.rejects(readBoundedResponse(new Response("x".repeat(11)), 10));
  await assert.rejects(readBoundedResponse(new Response("ok", { headers: { "Content-Length": "9999" } }), 10));
  await assert.rejects(readBoundedResponse(new Response("no", { status: 404 }), 10));
  assert.equal(Buffer.from(await readBoundedResponse(new Response("good"), 10)).toString(), "good");
});

test("fetches only the same-origin manifest and key without cookies or redirects", async () => {
  const requests = [];
  const fetcher = async (url, options) => {
    requests.push({ url: String(url), options });
    return new Response(String(url).endsWith("release-key.json") ? JSON.stringify(key) : signed());
  };
  const manifest = await fetchVerifiedRelease({ fetcher, cryptoProvider: webcrypto, baseUrl: "https://screenline.xiaogao.xyz/release.js" });
  assert.equal(manifest.tag, valid.tag);
  assert.deepEqual(requests.map((request) => request.url), [
    "https://screenline.xiaogao.xyz/releases/latest.json", "https://screenline.xiaogao.xyz/release-key.json",
  ]);
  requests.forEach(({ options }) => {
    assert.equal(options.credentials, "omit");
    assert.equal(options.redirect, "error");
    assert.equal(options.cache, "no-store");
  });
});

test("fetch aborts on timeout and rejects duplicate public-key fields", async () => {
  const waitingFetcher = (_url, options) => new Promise((_resolve, reject) => {
    options.signal.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError")), { once: true });
  });
  await assert.rejects(fetchVerifiedRelease({ fetcher: waitingFetcher, cryptoProvider: webcrypto,
    baseUrl: "https://screenline.xiaogao.xyz/release.js", timeout: 10 }));
  const duplicateKey = JSON.stringify(key).replace('"kty":"RSA"', '"kty":"RSA","kty":"RSA"');
  await assert.rejects(fetchVerifiedRelease({ fetcher: async (url) => new Response(
    String(url).endsWith("release-key.json") ? duplicateKey : signed()),
    cryptoProvider: webcrypto, baseUrl: "https://screenline.xiaogao.xyz/release.js" }));
});

function element(dataset = {}) {
  return { dataset, textContent: "", href: "#download", hidden: false, disabled: false,
    attributes: {}, setAttribute(name, value) { this.attributes[name] = value; },
    removeAttribute(name) { delete this.attributes[name]; }, addEventListener() {} };
}
function documentFixture() {
  const status = element(), notes = element(), retry = element();
  const anchors = [element({ download: "installer" }), element({ download: "installer" }),
    element({ download: "portable" }), element({ download: "source" }), element({ download: "checksums" })];
  const version = element(), size = element(), date = element();
  const github = { href: "https://github.com/xiaogao007/screenline-windows/releases/latest" };
  return { status, notes, retry, anchors, version, size, date, github,
    getElementById: (id) => ({ "release-status": status, "release-notes": notes, "retry-release": retry })[id],
    querySelectorAll: (selector) => ({ "a[data-download]": anchors,
      '[data-release="version"]': [version], '[data-release="size"]': [size], '[data-release="date"]': [date] })[selector] || [],
  };
}
async function settles(fixture) {
  for (let attempts = 0; attempts < 20 && fixture.status.dataset.state === "loading"; attempts += 1) {
    await new Promise((resolve) => setImmediate(resolve));
  }
  assert.notEqual(fixture.status.dataset.state, "loading");
}

test("successful UI updates all buttons and renders release notes as plain text", async () => {
  const fixture = documentFixture();
  const notes = '<img src=x onerror="alert(1)">';
  const manifest = await verify(signed({ ...valid, notes }));
  initReleaseClient(fixture, async () => manifest);
  await settles(fixture);
  assert.equal(fixture.status.dataset.state, "success");
  assert.equal(fixture.notes.textContent, notes);
  assert.equal(fixture.version.textContent, valid.tag);
  assert.equal(fixture.anchors[0].href, fixture.anchors[1].href);
  fixture.anchors.forEach((anchor) => {
    assert.ok(anchor.href.startsWith(RELEASE_BASE_URL));
    assert.equal(anchor.attributes["aria-disabled"], undefined);
  });
  assert.equal(fixture.date.hidden, true);
  assert.equal(fixture.retry.hidden, true);
  assert.equal(fixture.github.href, "https://github.com/xiaogao007/screenline-windows/releases/latest");
});

test("a failed refresh clears direct download links and exposes retry and fallback", async () => {
  const fixture = documentFixture();
  let fail = false;
  const client = initReleaseClient(fixture, async () => { if (fail) throw Error("signature"); return valid; });
  await settles(fixture);
  fail = true;
  await client.refresh();
  assert.equal(fixture.status.dataset.state, "error");
  assert.equal(fixture.version.textContent, "版本暂不可用");
  fixture.anchors.forEach((anchor) => {
    assert.equal(anchor.href, "#download");
    assert.equal(anchor.attributes["aria-disabled"], "true");
  });
  assert.equal(fixture.retry.hidden, false);
  assert.equal(fixture.retry.disabled, false);
  assert.equal(fixture.github.href, "https://github.com/xiaogao007/screenline-windows/releases/latest");
  fail = false;
  await client.refresh();
  assert.equal(fixture.status.dataset.state, "success");
});

test("unknown DOM download identifiers never become inherited object properties", async () => {
  const fixture = documentFixture();
  const unknown = element({ download: "constructor" });
  fixture.anchors.push(unknown);
  initReleaseClient(fixture, async () => valid);
  await settles(fixture);
  assert.equal(unknown.href, "#download");
  assert.equal(unknown.attributes["aria-disabled"], "true");
  assert.equal(fixture.status.dataset.state, "success");
});

function tags(html, name = "[a-z][a-z0-9-]*") {
  return [...html.matchAll(new RegExp(`<(${name})\\b([^>]*)>`, "gi"))].map((match) => {
    const attributes = Object.create(null);
    for (const attribute of match[2].matchAll(/([:\w-]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'=<>`]+)))?/g)) {
      attributes[attribute[1].toLowerCase()] = attribute[2] ?? attribute[3] ?? attribute[4] ?? "";
    }
    return { name: match[1].toLowerCase(), attributes };
  });
}

test("the actual page supplies every client DOM contract and a no-JavaScript fallback", async () => {
  const html = await readFile(new URL("../index.html", import.meta.url), "utf8");
  const elements = tags(html);
  const downloads = elements.filter(({ attributes }) => Object.hasOwn(attributes, "data-download"));
  assert.equal(downloads.length, 6);
  assert.deepEqual(downloads.map(({ attributes }) => attributes["data-download"]).sort(),
    ["checksums", "installer", "installer", "portable", "portable", "source"]);
  downloads.forEach(({ name, attributes }) => {
    assert.equal(name, "a");
    assert.equal(attributes.href, "#download");
  });
  const byId = (id) => elements.filter(({ attributes }) => attributes.id === id);
  assert.equal(byId("download").length, 1);
  assert.equal(byId("release-status").length, 1);
  assert.equal(byId("release-status")[0].attributes.role, "status");
  assert.equal(byId("release-notes").length, 1);
  assert.equal(byId("release-notes")[0].name, "pre");
  assert.equal(byId("retry-release").length, 1);
  assert.equal(byId("retry-release")[0].name, "button");
  assert.equal(byId("retry-release")[0].attributes.type, "button");
  assert.ok(Object.hasOwn(byId("retry-release")[0].attributes, "hidden"));
  assert.ok(elements.some(({ attributes }) => attributes["data-release"] === "version"));
  assert.ok(elements.some(({ attributes }) => attributes["data-release"] === "size"));
  elements.filter(({ attributes }) => attributes["data-release"] === "date").forEach(({ attributes }) => {
    assert.ok(Object.hasOwn(attributes, "hidden"));
  });
  assert.ok(elements.some(({ name, attributes }) => name === "a"
    && attributes.href === "https://github.com/xiaogao007/screenline-windows/releases/latest"
    && attributes.class?.includes("github-fallback")));
  assert.match(html, /<noscript>[\s\S]*?GitHub[\s\S]*?<\/noscript>/);
});

test("page execution and resources are compatible with a same-origin-only CSP", async () => {
  const html = await readFile(new URL("../index.html", import.meta.url), "utf8");
  const elements = tags(html);
  const scripts = [...html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script\s*>/gi)];
  assert.equal(scripts.length, 1);
  assert.equal(scripts[0][2].trim(), "");
  const script = tags(html, "script")[0];
  assert.equal(script.attributes.type, "module");
  assert.equal(script.attributes.src, "./release.js");
  elements.forEach(({ name, attributes }) => {
    assert.ok(!Object.keys(attributes).some((attribute) => /^on/i.test(attribute)), "No inline event handlers");
    assert.ok(!Object.hasOwn(attributes, "style"), "No inline style attributes");
    if (["script", "img"].includes(name)) {
      assert.equal(new URL(attributes.src, "https://screenline.xiaogao.xyz/").origin, "https://screenline.xiaogao.xyz");
    }
    if (name === "link" && ["stylesheet", "icon"].includes(attributes.rel)) {
      assert.equal(new URL(attributes.href, "https://screenline.xiaogao.xyz/").origin, "https://screenline.xiaogao.xyz");
    }
  });
  const css = await readFile(new URL("../styles.css", import.meta.url), "utf8");
  assert.doesNotMatch(css, /@import\b|@font-face\b|url\(\s*['"]?(?:https?:|data:|\/\/)/i);
  assert.doesNotMatch(source, /\beval\s*\(|new\s+Function\b|\.innerHTML\b|document\.write\b/);
});
