# Kiler.Crypto: the .NET side

Part of the `kiler-dotnet` skill. Read it with [SKILL.md](SKILL.md) when a task changes Kiler.Crypto or its tests. The cryptographic rules and formats are in the `kiler-crypto` skill; this file holds only how the project is built and tested.

## The project (M3)

- No files, no network, no JSON and no dependency on the other Kiler projects (`DependencyTests`). It takes and gives bytes and base64 strings; the models and their JSON are in Kiler.Core.
- Only what .NET has built in (`System.Security.Cryptography`), no package. Platform-neutral: no Windows calls (DPAPI comes after the exam, in Kiler.App behind an interface of Kiler.Core).
- Key bytes live in a pinned array (`GC.AllocateArray<byte>(…, pinned: true)`), so the GC does not leave copies of them, and are zeroed when the key is disposed (`CryptographicOperations.ZeroMemory`). A temporary buffer with a key, a derived key, a decrypted key or the UTF-8 of a password is pinned too and zeroed in a `finally`.
- The bytes of a `SecretKey` are read only through `SecretKey.Use`, which holds the lock that `Dispose` takes (M3.1): the automatic locking of M4 can dispose a key while a request encrypts with it, and without the lock the encryption would take the zeroed bytes as its key and succeed. The use must not keep the span; `AesGcm` copies the key when it is made, so `key.Use(Aes256Gcm.Create)` and the work after it outside the lock.
- The primitives are internal (`EncryptedValue`, `Purpose`, `PasswordKey`, `Aes256Gcm`, `Level`); only the types that Kiler.Core needs are public: `SecretKey`, `LevelCheck` and `FieldGroup` (M3.2), `LevelKeys`, `LockedKeys` and `RecoveryKey` (M3.3). A new purpose gets its label in `EncryptedValue.Label` and a golden value in the tests, as the labels are part of the format.
- A decrypted secret that becomes a key goes straight into a pinned buffer of the caller through the `EncryptedValue.Decrypt` that takes a destination span, which refuses a value of another length; the buffer is zeroed in a `finally` (M3.3, `LevelKeys.Unlock`).
- A set that comes from the share whole, such as the locked keys of a user file, gives what it can: `LevelKeys.Unlock` leaves out an entry that does not unlock, whatever the reason, and a wrong secret gives an empty set, not an exception (M3.3). A single value that the caller passes, such as a check or a group, keeps the rule above: null and a bad level are `ArgumentException` types.
- A level goes into a context only through `Level.Context`, which refuses a level below 1 (level 0 is not encrypted) and gives its digits (M3.2). The public API takes the level as an `int`.
- A decryption that answers yes or no, such as `LevelCheck.Verify`, catches only `CryptographicException`: an `ObjectDisposedException` of a key is a locked level, which M4 handles, and an `ArgumentException` is a bug of the caller (M3.2).
- A failed decryption is a `CryptographicException` with one message, which names no secret and no part of the value. The text of a value must be the canonical base64 of its bytes.

## Tests

- TDD: the tests are written first and seen to fail (SKILL.md, "Tests").
- PBKDF2 with 600 000 iterations takes a noticeable time per call: a test that needs many locks uses few, and the iterations are never lowered for a test, as the count is part of the format.
- A key, a password or a decrypted value is compared with `Assert.True` and a message without it (SKILL.md, "Tests"); a public test vector may be compared with `Assert.Equal`.
- A test that an input is refused or left out uses values that the code would accept without the guard: made with the key, the salt and the context of the test. Otherwise they fail at the tag anyway, and the broken guard passes, as the base64 of M3.1 and the salt length of M3.3 did at first.
- A break of a guard must change what the code does: `char.ToUpperInvariant` is no break of an ASCII-only upper case, as .NET's invariant casing maps no letter outside ASCII into it; the upper case of the culture is (M3.3).
