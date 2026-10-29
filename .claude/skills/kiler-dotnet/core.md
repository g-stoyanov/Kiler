# Kiler.Core: models, the session and permissions

Part of the `kiler-dotnet` skill. Read it with [SKILL.md](SKILL.md) when a task changes Kiler.Core or its tests. Kiler.Core calls the data layer, so read the bullets of [storage.md](storage.md) that the task needs too, such as "Documents in the upper layers", "The name check", "Watching and threads" and "Offline". The cryptographic rules are in the `kiler-crypto` skill.

## The project (M4)

- Kiler.Core connects Kiler.Crypto and Kiler.Storage: the models of the documents (M4: levels, settings, users; M5: templates, records, stashes), the session and the permissions. It has no Windows code: the current account, DPAPI and Explorer are interfaces here, implemented in Kiler.App (SKILL.md, "Windows parts"), and the tests use fakes.
- The tests are written in the same task, after the code; TDD is for Kiler.Crypto and Kiler.Storage. The API tests of the endpoints are in Kiler.App.Tests (host.md).

## Models

- A model is read from a `Document` and written to its content with a source-generated JSON context of Kiler.Core, case-sensitive, not the web defaults of `KilerJsonContext` (storage.md, "Documents in the upper layers"). `id` and `version` come only from `Document.Key` and `Document.Version`. Content stays within `DocumentFormat.MaxSize` and `MaxDepth`; over them, `New` and `Next` throw `ArgumentException`.
- Convert each `Document` to its model and drop it. The layer keeps only its index in memory and the bytes on the disk, and a start or a load gives a new `Document` for each document (M2.7). The budget of the copy bounds the bytes, but a crafted 1 MiB document can take about 30 times as much once its nodes are walked (M2.2), so keep no `JsonNode` of it.
- A document of the share that does not fit its model is invalid, with a reason, not an exception: anyone can write the share (the threat model in the `security-reviewer` agent).
- Every id, and the name of an account, is made in lowercase and passes `StorageName.Check` before a key is built from it (storage.md, "The name check").
- A record type that holds a password, a key or a decrypted value overrides `ToString()` (SKILL.md, "Style").

## Calling the layer

- Write through `DataCopy` (`Create`, `Replace`, `Delete`, `RestoreFromTrash`), never through `DataFolder`, whose writes are internal: the copy takes what it wrote as its own (M2.7a). `LoadResult.Outside` is where an edit in place, a version out of order and a damaged file show. An owner of `null` after a write of this Kiler means unknown until the next load.
- A `Conflict` whose `OnDisk.Version` is the version the write changes is a slot in the way: another write under way, a leftover younger than 2 minutes, or one that is never taken over. Writing again with that version meets it again, so nothing repeats it in a loop, and the answer asks to try again in a moment rather than give two equal versions (M2.4).
- While the share cannot be reached (`DataCopy.Offline`), the writes give `WriteOutcome.Offline` before they touch it. The answer says that the shared folder cannot be reached and that the change can be made once it is back, and nothing repeats it. A write that cannot reach the share before a load has said so still throws (M2.8a).
- A deleted id is not created again (`Exists`): it comes back from the trash and is written over, or a new id is taken (M2.5).
- `DataWatch.Loaded` gives each load on the thread of the watch, after the load has let go of the copy, so a handler may call the copy. A handler does not throw, which ends the process, and keeps its work short. After `Failed`, and at each `OfflineChanged`, read `Documents()` and `Trash()` again: what the load took is in the copy, and no later load gives it. Each public member of `DataCopy` waits for a load under way, up to about 60 s for a read blocked over SMB; while offline, only for the load that found the share gone (M2.8, M2.8c).

## Keys and the session (M4)

- The decisions are in `docs/tasks/M4.md`, "Decisions for the module": one session per process, whose levels are the keys it holds, never the `level` of the user file.
- A level key of the session is a `SecretKey`, disposed by a lock, a sign-out and the stop. A request that meets `ObjectDisposedException` from a key (`SecretKey.Use`) answers as locked, not as an error (M3.1); a decryption that answers yes or no catches only `CryptographicException` (crypto.md).
- The caller owns the keys that `LevelKeys.Unlock` gives: keep each one only after `LevelCheck.Verify`, and dispose the rest (`kiler-crypto`, "Rules").
