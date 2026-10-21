---
name: kiler-crypto
description: Kiler's cryptography rules. Use when writing or reviewing code that encrypts or decrypts data, derives keys, or handles passwords, keys or other secrets.
---

# Kiler: cryptography

Decisions come from sections 7 and 12 of the design (`KILER_DESIGN.md`, local, not in git) and from M3 in `docs/PLAN.md`. Anything not decided yet is listed under "Deferred". Do not invent it; if a task needs such a decision, ask.

## Model

- Each level has **one** key of 32 random bytes, shared by the users of that level.
- **Level 0 is not encrypted.**
- A record's fields are grouped by level, and each group is encrypted with the key of its level. Groups are independent: an editor changes only the groups up to their own level.
- Each user keeps a copy of the keys up to their level, locked with a key derived from their personal password.
- **The password is not stored anywhere.** A wrong password simply does not unlock the keys.
- A level's `check` value is a known text encrypted with the level key. It proves that an unlocked key is genuine.
- The admin stamp is an HMAC-SHA256 over the user's file, with a key that only admins have.

## Algorithms

Only what is built into .NET. No external crypto libraries and no home-made algorithms.

| Purpose | What |
|---|---|
| Encryption | `AesGcm`, 32-byte key, 12-byte nonce, 16-byte tag |
| Key from a password | `Rfc2898DeriveBytes.Pbkdf2`, SHA-256, 600,000 iterations, a random salt per user |
| Random values | `RandomNumberGenerator` |
| Stamp | `HMACSHA256` |
| Update signature | `ECDsa`, curve P-256, SHA-256; only the public key is in the exe (decided after M1.0b) |
| "Remember me" | DPAPI (`ProtectedData`, current user), behind an interface; implemented in Kiler.App |

## Rules

- **A new random nonce for every encryption.** A nonce is never reused with the same key.
- If the tag does not match, the data was tampered with: fail, with no partial result.
- An encrypted value carries a format version, so the algorithm can change later.
- Secret values (tokens, MACs, check values) are compared in constant time: `CryptographicOperations.FixedTimeEquals`.
- Keep keys and passwords in `byte[]`, not `string`, where possible. Zero them after use with `CryptographicOperations.ZeroMemory`.
- Passwords, keys and decrypted values never go into logs, exception messages or `ToString()`.
- Forbidden: `System.Random` for security, MD5, SHA-1, ECB, CBC without a MAC, home-made schemes.
- Nobody encrypts a field above their own level, because they do not have the key. The server checks this too.

## Tests

Tests are written first (TDD) and cover:

- encrypting and decrypting returns the same text;
- changing a single byte is detected;
- a wrong password does not unlock the keys;
- the `check` value;
- changing the password keeps the same level key;
- recovery with the recovery key;
- known test vectors for PBKDF2-SHA256 and AES-GCM;
- an older format version can still be read.

## Deferred (decided at the start of M3)

| Question | Note |
|---|---|
| Exact byte layout of the format | version, nonce, ciphertext and tag; base64 |
| Binding a group to the record id and level (associated data in AES-GCM) | an addition to the design; needs a decision |
| Where and how the admin stamp key is kept | |
| What exactly the stamp signs | a canonical form of the user's file, without the `stamp` field itself |
| Recovery key format | a long, readable code, shown only once |
| Salt length | |
| Where the private key for signing updates is kept, and how a release is signed | never in the repo or the shared folder |
