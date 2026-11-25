/*
 * SHA-256 (ASCII input). Public domain, https://geraintluff.github.io/sha256/
 * Нужен для контрольной суммы хеш-строки RP2 — совместим с C# SHA256
 * (checksum = первые 8 байт SHA256 от base64-payload в hex, 16 символов).
 * crypto.subtle не используем: он недоступен на http:// по локальной сети.
 */
/* exported sha256 */
function sha256(ascii) {
  function rightRotate(value, amount) {
    return (value >>> amount) | (value << (32 - amount));
  }

  var mathPow = Math.pow;
  var maxWord = mathPow(2, 32);
  var result = '';

  var words = [];
  var asciiBitLength = ascii.length * 8;

  var hash = (sha256.h = sha256.h || []);
  var k = (sha256.k = sha256.k || []);
  var primeCounter = k.length;
  var isComposite = {};
  for (var candidate = 2; primeCounter < 64; candidate++) {
    if (!isComposite[candidate]) {
      for (var i = 0; i < 313; i += candidate) {
        isComposite[i] = candidate;
      }
      hash[primeCounter] = (mathPow(candidate, 0.5) * maxWord) | 0;
      k[primeCounter++] = (mathPow(candidate, 1 / 3) * maxWord) | 0;
    }
  }

  ascii += '\x80';
  while ((ascii.length % 64) - 56) ascii += '\x00';
  for (var j = 0; j < ascii.length; j++) {
    var c = ascii.charCodeAt(j);
    if (c >> 8) throw new Error('sha256: только ASCII (payload — base64, это ок)');
    words[j >> 2] |= c << (((3 - j) % 4) * 8);
  }
  words[words.length] = (asciiBitLength / maxWord) | 0;
  words[words.length] = asciiBitLength;

  for (var j2 = 0; j2 < words.length;) {
    var w = words.slice(j2, (j2 += 16));
    var oldHash = hash;
    hash = hash.slice(0, 8);

    for (var i2 = 0; i2 < 64; i2++) {
      var w15 = w[i2 - 15], w2 = w[i2 - 2];
      var a = hash[0], e = hash[4];

      var temp1 = hash[7]
        + (rightRotate(e, 6) ^ rightRotate(e, 11) ^ rightRotate(e, 25))
        + ((e & hash[5]) ^ (~e & hash[6]))
        + k[i2]
        + (w[i2] = i2 < 16 ? w[i2] : (
            w[i2 - 16]
            + (rightRotate(w15, 7) ^ rightRotate(w15, 18) ^ (w15 >>> 3))
            + w[i2 - 7]
            + (rightRotate(w2, 17) ^ rightRotate(w2, 19) ^ (w2 >>> 10))
          ) | 0);

      var temp2 = (rightRotate(a, 2) ^ rightRotate(a, 13) ^ rightRotate(a, 22))
        + ((a & hash[1]) ^ (a & hash[2]) ^ (hash[1] & hash[2]));

      hash = [(temp1 + temp2) | 0].concat(hash);
      hash[4] = (hash[4] + temp1) | 0;
    }

    for (var i3 = 0; i3 < 8; i3++) {
      hash[i3] = (hash[i3] + oldHash[i3]) | 0;
    }
  }

  for (var i4 = 0; i4 < 8; i4++) {
    for (var j3 = 3; j3 + 1; j3--) {
      var b = (hash[i4] >> (j3 * 8)) & 255;
      result += (b < 16 ? '0' : '') + b.toString(16);
    }
  }
  return result;
}
