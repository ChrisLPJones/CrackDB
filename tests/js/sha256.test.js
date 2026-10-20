// Run by hand: node tests/js/sha256.test.js
const crypto = require('crypto');
const Sha256 = require('../../src/HoarderDB.Web/wwwroot/sha256.js');
let bad = 0;
const check = (name, got, want) => { if (got !== want) { bad++; console.log('FAIL', name, got, want); } else console.log('ok  ', name); };
const hex = (u8, chunk) => { const h = new Sha256(); for (let i = 0; i < u8.length || i === 0; i += chunk) h.update(u8.subarray(i, i + chunk)); return h.hex(); };
const ref = (u8) => crypto.createHash('sha256').update(u8).digest('hex');
const enc = (s) => new TextEncoder().encode(s);
check('empty', new Sha256().hex(), 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855');
check('abc', hex(enc('abc'), 1000), 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad');
check('448-bit', hex(enc('abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq'), 7), '248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1');
const m = new Sha256(); const a = new Uint8Array(1000).fill(97); for (let i = 0; i < 1000; i++) m.update(a);
check('million a', m.hex(), 'cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0');
for (const len of [1, 55, 56, 57, 63, 64, 65, 119, 120, 127, 128, 129, 1000, 100003]) {
  const u = crypto.randomBytes(len);
  for (const chunk of [1, 13, 64, 65, 4096]) check('len ' + len + ' chunk ' + chunk, hex(u, chunk), ref(u));
}
console.log(bad ? bad + ' FAILED' : 'ALL PASS'); process.exit(bad ? 1 : 0);
