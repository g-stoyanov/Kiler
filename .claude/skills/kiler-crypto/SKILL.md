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
- **Private records** (design section 7.10) use a **personal key** of 32 random bytes:
  - it is created by the user's own exe with their first private record, never by the admin's exe, and the admin never gets it;
  - it is kept in `private\<account>\key.json`, not in the user's file, locked twice: with the password and with a personal recovery code;
  - a private record is encrypted whole with it, with the owner and the record id as associated data;
  - it is never remembered (no DPAPI), like the levels with `"remember": false`.

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
- If the personal key does not unlock, fail. Never create a new personal key in its place: the old private records would become unreadable without anyone noticing.

## Formats (decided at the start of M3)

The decisions are in `docs/tasks/M3.md`, "Decisions for the module".

- **An encrypted value:** base64 (standard alphabet, with padding) of `[version: 1 byte][nonce: 12][ciphertext][tag: 16]`. Version 1 is AES-256-GCM, at least 29 bytes. A new algorithm gets a new version, and the old versions stay readable.
- **Associated data:** the version byte, then the label of the purpose and the parts of the context, each as strict UTF-8 (a lone surrogate is refused) with its length before it in 4 bytes, big-endian. Every encryption has a purpose and its context, so a value cannot be moved to another record, level or use:

  | Purpose | Context | What |
  |---|---|---|
  | `group` | record id, level | a group of fields of a shared record (`protected."<level>"`) |
  | `check` | level | the check value of a level: the UTF-8 of `Kiler level check` (M3.2), compared in constant time |
  | `level-key` | level | a level key locked with a password or the recovery key |

  The private records add their own after the exam: the owner and the record id (design 7.10).
- **Salt:** 16 random bytes, new at every lock, also when the same keys are locked again.
- **A locked set of level keys** (`LevelKeys`, M3.3): the `salt` and `keys` of a user file (design 7.3). The salt is base64 of 16 bytes, and each key is an encrypted value of its 32 bytes, with the key from the secret and the salt, purpose `level-key` and its level as context, under the level's digits. A set locked with the recovery key has the same format.
- **A password** goes into PBKDF2 as UTF-8, as typed, and those bytes are zeroed after use.
- **The recovery key:** 20 random bytes as 32 characters of Crockford Base32 (`0123456789ABCDEFGHJKMNPQRSTVWXYZ`), shown in 8 groups of 4 joined by `-`. A typed key is read without regard to case, hyphens and spaces, with O read as 0 and I and L as 1; anything else is refused. The 32 upper-case characters are the secret, used as a password (salt and PBKDF2). The personal recovery code (design 7.10) has the same format.
- **Kiler.Crypto knows bytes and base64 strings only:** no files, no JSON, no models. Kiler.Core gives it the UTF-8 of a group of fields.

## Tests

Tests are written first (TDD) and cover:

- encrypting and decrypting returns the same text;
- changing a single byte is detected;
- a wrong password does not unlock the keys;
- the `check` value;
- changing the password keeps the same level key;
- recovery with the recovery key;
- known test vectors for PBKDF2-SHA256 and AES-GCM;
- an older format version can still be read;
- the personal key unlocks with the password and with the recovery code;
- a private record does not decrypt under another id or another owner.

Known test vectors come from their source (RFC 7914 for PBKDF2-HMAC-SHA256, the test cases of the GCM specification for AES-GCM), through `docs-researcher`, never from memory.

## Deferred until after the exam (PLAN section 10)

| Question | Note |
|---|---|
| Where and how the admin stamp key is kept | the admin seal of PLAN M3 step 6 |
| What exactly the stamp signs | a canonical form of the user's file, without the `stamp` field itself; `levels.json` too (PLAN section 10, decided after M3.2) |
| Where the private key for signing updates is kept, and how a release is signed | never in the repo or the shared folder (M3 step 11) |
