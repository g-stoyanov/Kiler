# Kiler: the API of templates, stashes and records

The contract between Kiler and the page for the records, written in M5.1. The later tasks of M5 build it, and M6 writes `src/ui/src/api.ts` from it. A change of the API goes here first, then into the code (decided at the start of M5, [tasks/M5.md](tasks/M5.md)).

It covers what is built until the exam ([PLAN.md, section 10](PLAN.md#10-deferred-until-after-the-exam)). "Not in this contract" at the end lists what comes after it. The endpoints of the earlier modules (the health check, the event stream, the data, the wizard, the session, the password and the admin screens) are in the code and in the `kiler-dotnet` skill (`host.md`).

The examples are made up.

## Conventions

- **JSON.** The members are camelCase, as in every answer of Kiler (`KilerJsonContext`, the web defaults). A member that does not apply is `null`, never left out. A request member that the endpoint does not know is ignored, so the dates and the authors of a record in a request change nothing (PLAN M5 step 3).
- **Ids.** The id of a record or a stash is 8 characters of lowercase Crockford Base32, such as `k3x9q2mf`. The id of a template is the name of its file in `templates\`, such as `db`. An id in a path that cannot be an id is answered as one that is not there (404).
- **Dates** are UTC in ISO 8601 with `Z`, such as `2026-10-21T11:32:00Z`. Only Kiler sets them. The page shows them in local time (design 6.1).
- **Rights.** Each endpoint states its `Need` (`core.md`, "Keys and the session"):
  - `Reader` is a guest while guest mode is on, who reads level 0, or a session, which reads the levels of its keys;
  - `Editor` is a session with `canEdit`.

  A request that does not get what its endpoint needs is 403 with the sentence of `AccessRules.Message`. A session that was locked meanwhile is 403 with `AccessRules.Locked` (`host.md`, "Rights"). The other 403s of this contract are of the same kind: something that this session may not do. So after any 403 the page reads the session again (`kiler-ui`, "API"), and the page hides what the session may not do, so these 403s come only from a race. A request that the page built wrong is 400.
- **Activity.** Every endpoint here is activity for the automatic lock (`host.md`, "Automatic lock"); none is `.NotActivity()`. The one exception is a `GET` with `Kiler-Activity: none`, the read of a page that follows a `changed` event ("The changes of other users" at the end).
- **Refusals** are an `ErrorResponse`, `{ "error": "<sentence>" }`, whose sentence is for the user (`kiler-ui`, "API"):
  - **400**: the request does not fit. A write also gives `field`, `{ "error": "<sentence>", "field": "host" }`: the key of the field that does not fit (`stash`, `name`, `description`, `tags` or a field of the template), or `null`. A record too large for a document (`DocumentFormat.MaxSize`; the encrypted groups are about a third larger than their fields) is 400 with `field` `null`. A body that is not JSON, or a member of another JSON type than the contract gives (such as a level that is not a whole number, or a list given as a string), is 400 without a body, as ASP.NET Core answers it (M5.9). A number given as a string, such as `"version": "7"`, is read as the number, as the web defaults of the JSON allow.
  - **403**: see "Rights".
  - **404**: the record is not there, or it is in the trash; where a path names a version, that one is not there. `api.ts` already reads it as a refusal.
  - **409**: not now.
    - While Kiler has no open copy: the sentence of `SessionApi.NoData`.
    - A write while the shared folder cannot be reached: "The shared folder cannot be reached. The change can be made once it is back." (`WriteOutcome.Offline`, M2.8a).
    - A write that meets the slot of another write, a `Conflict` at the version that it changes (M2.4): "The record is being written now. Try again in a moment."
    - A write over an older version: the conflict ("Conflict" below). Only this 409 has `current`.
    - The other outcomes of a write in the layer (a file that another program holds, a damaged file, a version that cannot go higher, an id that is live again): "The record could not be written to the shared folder. Try again in a moment.", as `AdminApi.Message` says for a user file.
- **Offline.** The reads answer from the local copy, also while the shared folder cannot be reached.

## Shapes

### Author

```json
{ "account": "demo.editor", "name": "Demo Editor" }
```

`account` is the account that Kiler wrote. `name` is the display name of its user file, `null` when the team has no user file of it that fits.

### A stash of a path

```json
{ "id": "a1b2c3d4", "name": "Payments" }
```

`name` is `null` when the stash cannot be read: it is missing, or it does not fit. The record still shows at its place.

### Field

Every field of a record, and its description, comes as a field:

```json
{ "key": "host", "level": 1, "kind": "value", "value": "db01.example.test", "levelName": null }
```

- `key`: the key of the field in the template; `description` for the description.
- `level`: the level of the field in this record, its group or 0 in `public`. A field without a value has the level that a value would get, its default ("Levels of a field").
- `kind`:
  - `value`: the session reads it, and `value` holds it;
  - `empty`: the field has no value;
  - `secret`: a `secret` with a value. Only the request of a secret gives the value; no other answer has it;
  - `locked`: the field is at a level that the session does not hold. `levelName` is the name of that level in `levels.json`, for "🔒 requires level Prod", or `null` when `levels.json` cannot be read;
  - `unreadable`: its group does not decrypt (damaged, or moved from another record or level), or what it decrypts to does not fit (M5.3).
- A reader below a group tells `locked` from `empty` by the lists of `fields` in the file, which are in clear (design 5.2): a key that no list names and `public` does not have is `empty`.

### Values by type

The types are those of design 6.4.

| Type | `value` in an answer | In a request |
|---|---|---|
| `text`, `multiline`, `markdown`, `doc` | a string | a string |
| `select` | a string, one of `options` | a string, one of `options` |
| `number` | a JSON number | a JSON number |
| `date` | an ISO 8601 date, such as `"2026-10-21"` | the same |
| `url` | a string | an absolute `http` or `https` address |
| `code` | a string; `{ "language": "bash", "code": "…" }` when the template gives `languages` (design 6.3) | the same, `language` one of `languages` |
| `ref` | `{ "id": "k3x9q2mf", "name": "Payments production database" }`; `name` is `null` when that record has gone (deleted, or never there) | the id of a record of `refTemplate` |
| `secret` | never (`kind` is `secret`) | a string |

- In a request, `null` and `""` are no value, and neither is a code whose `code` is `""` (M5.4).
- In an answer, a value keeps the shape that it was written in, so a template changed after its records can give another shape than its type says, such as a number of a `text` or a `code` without `language`. A `ref` is the object above whenever its template says `ref` and the value is a text (M5.6).
- In the file, a `secret` is `{ "secret": "<value>" }` in its group (decided by the user in M5.3, design 5.2). The group is encrypted and authenticated, so Kiler knows the field is a `secret` also when the template on the share, which anyone can write, gives it another type: a field is a `secret` when its template says so or its group marks it.
- Anyone can write the share, so the page opens a `url` only when it is `http` or `https`, also one that Kiler did not check.

### List item

An item of `GET /api/records`:

```json
{
  "id": "k3x9q2mf",
  "template": "db",
  "version": 7,
  "stash": [{ "id": "a1b2c3d4", "name": "Payments" }, { "id": "e5f6g7h8", "name": "Prod" }],
  "name": "Payments production database",
  "description": { "key": "description", "level": 0, "kind": "value", "value": "The main database of the payments", "levelName": null },
  "tags": ["payments"],
  "fields": [{ "key": "host", "level": 1, "kind": "locked", "value": null, "levelName": "Team" }],
  "updatedBy": { "account": "demo.editor", "name": "Demo Editor" },
  "updatedAt": "2026-10-21T11:32:00Z"
}
```

`fields` are the fields of the template with `list`, in the order of the template, each as in a record ("Field", "Values by type"): a `code` and a `ref` too, and a long value whole, which the page shortens (decided in M5.6). A `secret` is never there, whatever its `list`.

### Record

The answer of `GET /api/records/{id}` and of the writes: the list item without its `fields`, and with these.

```json
{
  "id": "k3x9q2mf",
  "template": "db",
  "version": 7,
  "stash": [{ "id": "a1b2c3d4", "name": "Payments" }, { "id": "e5f6g7h8", "name": "Prod" }],
  "name": "Payments production database",
  "description": { "key": "description", "level": 0, "kind": "value", "value": "The main database of the payments", "levelName": null },
  "tags": ["payments"],
  "levels": [1, 3],
  "fields": [
    { "key": "env", "level": 0, "kind": "value", "value": "prod", "levelName": null },
    { "key": "host", "level": 1, "kind": "value", "value": "db01.example.test", "levelName": null },
    { "key": "port", "level": 1, "kind": "value", "value": 1521, "levelName": null },
    { "key": "tns", "level": 1, "kind": "empty", "value": null, "levelName": null },
    { "key": "user", "level": 3, "kind": "locked", "value": null, "levelName": "Prod" },
    { "key": "password", "level": 3, "kind": "locked", "value": null, "levelName": "Prod" }
  ],
  "createdBy": { "account": "demo.admin", "name": "Demo Admin" },
  "createdAt": "2026-09-30T08:10:00Z",
  "updatedBy": { "account": "demo.editor", "name": "Demo Editor" },
  "updatedAt": "2026-10-21T11:32:00Z"
}
```

- `levels`: the levels of the groups of the record, the keys of `protected` in its file; a record whose `fields` names other levels does not fit (M5.3). By them the page tells whether to offer the delete and the restores ("Decided in M5.1"). Kiler decides the right itself, from the record on the share at the version that the request writes, never from what the page sends.
- `fields`: every field of the template, in its order. A field in the file that the template does not have is not shown.
- A record whose template is missing or does not fit has only its header. Its `fields` is `[]`, as Kiler cannot tell which of its fields is a `secret`. The search does not look into its fields, and an edit is refused (409). It can be deleted.

### Trash item

An item of `GET /api/trash`: the list item with `levels`. The trash does not say who deleted a record, or when: a delete moves the file as it is (M2.5).

### Version

An item of `GET /api/records/{id}/history`:

```json
{ "version": 6, "updatedBy": { "account": "demo.admin", "name": "Demo Admin" }, "updatedAt": "2026-10-20T09:00:00Z", "levels": [1, 3] }
```

`updatedBy` and `updatedAt` are those of that version, who made it and when; `levels` are the levels of its groups, by which the page offers its restore ("Decided in M5.1").

### Template

```json
{
  "id": "db",
  "name": "Server / database",
  "icon": "database",
  "fields": [
    { "key": "env", "label": "Environment", "type": "select", "level": 0, "levelByEnv": null, "required": false, "filter": true, "list": false,
      "options": ["prod", "test", "dev"], "language": null, "dialect": null, "languages": null, "refTemplate": null },
    { "key": "password", "label": "Password", "type": "secret", "level": null, "levelByEnv": { "prod": 3, "test": 2, "dev": 1 }, "required": false, "filter": false, "list": false,
      "options": null, "language": null, "dialect": null, "languages": null, "refTemplate": null }
  ]
}
```

- The members are those of design 6.2 to 6.5. `required`, `filter` and `list` are `false` when the file leaves them out. `level` is `null` when the field has only `levelByEnv`. The settings that do not belong to the type of the field are `null`.
- A template that does not fit is not used (`core.md`, "Models"). Besides the rules of M5.2 (`core.md`, "Templates, stashes and records"), it does not fit when a `secret` is below level 1 (design 7.1: passwords are at level 1 or higher) or has `list` or `filter`.

### Conflict

The 409 of a write over an older version:

```json
{ "error": "The record has been changed meanwhile.", "current": { "id": "k3x9q2mf", "version": 8 } }
```

- `current` is the whole record as `GET /api/records/{id}` gives it to this session now (shortened above).
- The page says who changed the record and when (`current.updatedBy`, `current.updatedAt`), and shows the two versions side by side, the edit and `current`, with the rows marked where they differ (M6.20). "Keep mine" sends the edit again with `current.version`; "Take theirs" shows `current` without a new request.
- A delete and a restore of an old version over an older version get the same answer.
- A write to a record that has gone meanwhile is 404, not a conflict.

## Levels of a field

These rules are for M5.4 and M5.8; the page follows them in its form.

- **The default level** of a field is its `levelByEnv` for the `env` of the record, when `env` has a value that `levelByEnv` names; else its `level`; else the highest level of its `levelByEnv`, so an `env` left empty does not lower a password.
- **A raise.** A write may ask a level for a field (`levels`). The level of the field is then the higher of its default and the level asked, so asking a lower level takes a raise back to the default.
- **In an edit**, a field that `levels` leaves out keeps its raise: when its current level is above its default under the `env` that the record had, that level counts as asked. So the page sends in `levels` only the levels that the editor changed, and a change of `env` moves only the fields that are not raised.
- A field with a value at a level above the top level of the author is 400. A field without a value is in no group, so its level does not count.
- The stash path, the name, the tags and `env` stay at level 0 (design 6.1 and 6.6). A level asked for one of them is 400. The description is at 0 unless it is raised.

## Endpoints

| Method and path | Need | What | Task |
|---|---|---|---|
| `GET /api/templates` | `Reader` | the templates | M5.2 |
| `GET /api/levels` | `Reader` | the names of the levels | M5.2 |
| `GET /api/records` | `Reader` | the search, the filters, the tree and the facets | M5.6 |
| `GET /api/records/{id}` | `Reader` | a record | M5.7 |
| `POST /api/records/{id}/secret` | `Reader`, and the endpoint refuses a guest | the value of a `secret` | M5.7 |
| `POST /api/records` | `Editor` | a new record | M5.9 |
| `PUT /api/records/{id}` | `Editor` | an edit | M5.9 |
| `GET /api/stashes` | `Reader` | the stashes for a position of the path | M5.9 |
| `POST /api/stashes` | `Editor` | a new stash | M5.9 |
| `GET /api/tags` | `Reader` | the tag suggestions | M5.9 |
| `DELETE /api/records/{id}?version=<n>` | `Editor` | a delete | M5.10 |
| `GET /api/trash` | `Reader` | the trash | M5.10 |
| `POST /api/trash/{id}/restore` | `Editor` | a restore from the trash | M5.10 |
| `GET /api/records/{id}/history` | `Reader` | the versions of a record | M5.11 |
| `POST /api/records/{id}/history/{version}/restore` | `Editor` | a restore of an old version | M5.11 |

Each endpoint can also give the 403s of "Rights" and the 409 without data; they are not repeated below.

### `GET /api/templates`

```json
{ "templates": [ { "id": "db", "name": "Server / database", "icon": "database", "fields": [] } ], "invalid": [ { "id": "old", "reason": "The template has no fields." } ] }
```

- `templates`: the templates that fit, by name.
- `invalid`: the files of `templates\` that do not fit, each with its reason, which names a JSON member and never repeats a value of the share (`core.md`, "Models").

### `GET /api/levels`

```json
{ "levels": [ { "level": 0, "name": "Guest" }, { "level": 1, "name": "Team" }, { "level": 2, "name": "Test" }, { "level": 3, "name": "Prod" } ] }
```

The levels 0 to the top level of `levels.json`, for the names in the form (the raise) and in the filters. 409 when `levels.json` cannot be read; `GET /api/data` says why.

### `GET /api/records`

The query, every part optional, and a part that is empty counts as left out; without any, every record that the session reads:

- `q`: the search text. A record matches when its search text has every word, without regard to case, in Cyrillic and in Latin; the words are split at white space (design 11). The search text of a record is its name, its description, its tags, the names of the stashes of its path, and the text of each field that the session reads, never a `secret`;
- `stash`: the path of the chosen node, 1 to 4 ids joined by commas. A record matches when its path starts with it;
- `template`: the id of a template;
- `tag`: a tag, repeated for each one, such as `tag=payments&tag=reports`. A record matches when it has every one, without regard to case (design 8.1);
- `field.<key>`: a value of a field with `filter`, one per key, such as `field.env=prod`. A record matches when its template has the field with `filter`, and the text of the value that the session reads in it is that one, without regard to case.

The text of a value, for the search, the filters and the facets (decided in M5.6): a text as it is, a number as JSON writes it, the code of a `code` without its language, and the name of the record of a `ref`, which has none when that record has gone. So a `doc`, a `markdown` and a `code` with `filter` count by their whole text, and a `ref` by the name that the page shows.

A record whose file does not fit (M5.3) is in neither the list, the tree nor the facets, as it has no name or path to show (decided in M5.6). A record whose template is missing or does not fit is there with its header ("Record"): the search reads its name, description, tags and stashes, and no `field.<key>` matches it.

The answer:

```json
{
  "records": [],
  "tree": [ { "id": "a1b2c3d4", "name": "Payments", "count": 30, "children": [ { "id": "e5f6g7h8", "name": "Prod", "count": 9, "children": [] } ] } ],
  "facets": {
    "templates": [ { "id": "db", "count": 12 } ],
    "tags": [ { "tag": "payments", "count": 5 } ],
    "fields": [ { "key": "env", "values": [ { "value": "prod", "count": 4 } ] } ]
  }
}
```

- `records`: list items of every record that matches everything, without paging, by name without regard to case.
- `tree`: the nodes of the paths of the records that match everything but `stash`, so the tree keeps every branch with results while a node is chosen (design 6.6). `count` counts those records at the node and under it, and a node without any is not there. The nodes are by name without regard to case, a stash that cannot be read last; `name` is `null` for it.
- `facets`: each counts the records that match `q`, `stash` and every filter but its own, so each choice shows how many records it gives. `templates` counts without `template`, and each key of `fields` without its own `field.<key>`. `tags` counts with the tags chosen, as a record has every one. `fields` holds the fields with `filter` of the templates, by key, never a `secret`. A value is its text, and only values that the session reads count. The templates are by id, and the tags and the values by their text without regard to case; a tag or a value written in several cases counts once, in one of them.
- 400: a `stash` that is not 1 to 4 ids, and a part other than `tag` given more than once.

### `GET /api/records/{id}`

The record. 404 when it is not there, and when its file does not fit (M5.3), which the list leaves out too (decided in M5.7).

### `POST /api/records/{id}/secret`

The value of a `secret`, for "show" and "copy" (design 8.2).

```json
{ "field": "password", "use": "show" }
```

- `use` is `show` or `copy`. One request serves both and says which it is (decided by the user in M5.1): the audit of Prod after the exam (PLAN section 10) records it, and until then it is only checked.
- The answer is `{ "value": "<the password>" }`, with `Cache-Control: no-store`. Kiler decrypts the group again for it and keeps the value nowhere (M5.7). The page keeps it only while it shows or copies it (`kiler-ui`, "Secret values").
- The value is a text. A `secret` as Kiler writes it is one. A value that its group does not mark, where the template says `secret`, such as a number written before the template changed, is its text ("The text of a value" under `GET /api/records`). A `secret` in `public`, which only a file changed by hand has, is answered from there, as it is in clear on the share (decided in M5.7).
- 400: a `use` other than these two, or a field that the template does not have (a `field` left out too) or that is not a `secret`.
- 403: a guest, with "Sign in first." (`AccessRules.Message(AccessRefusal.NotSignedIn)`); a field at a level that the session does not hold, whatever its type, as its group may mark it as a `secret`.
- 404: the record is not there, or its file does not fit.
- 409: the field has no value now, or its group does not decrypt.

### `POST /api/records`

A new record:

```json
{
  "template": "db",
  "stash": ["a1b2c3d4", "e5f6g7h8"],
  "name": "Payments production database",
  "description": "The main database of the payments",
  "tags": ["payments"],
  "fields": { "env": "prod", "host": "db01.example.test", "port": 1521, "user": "pay_read", "password": "made-up-password" },
  "levels": { "description": 2 }
}
```

- `template`: a template that fits.
- `stash`: 1 to 4 ids of stashes that are there, without gaps or repeats (design 6.6).
- `name`: not blank. The name and each tag are trimmed; a blank tag is left out, and the tags are kept once each, without regard to case.
- `fields`: the fields of the template by key, each a value of its type ("Values by type"). A field left out has no value. A field with `required` needs a value (design 6.5). A key that the template does not have is 400.
- `levels`: the levels asked, by key ("Levels of a field"). A field left out has its default. A key that is neither `description` nor a field of the template is 400 with `field` `null`, and a level that is not from 0 to 9 is 400 with that key (M5.4).
- Kiler sets the id, the dates and the authors (design 6.1).
- The answer is 201 with the record, and `Location: /api/records/<id>`.
- 400, with `field`: a value that does not fit, and a field with a value at a level above the author's. A value of `fields` that is not a text, a number, `null` or a code object is 400 with its key (M5.9).
- 404: the record was written, but a load took it away before the answer, as another Kiler deleted it at once (decided in M5.9). The page reads the list again.
- 409: offline; the slot of another write; the other outcomes of a write.

### `PUT /api/records/{id}`

An edit, over the version that the editor read. The request is that of a new record without `template`, which does not change, and with `version`:

```json
{ "version": 7, "stash": ["a1b2c3d4", "e5f6g7h8"], "name": "Payments production database", "description": "The main database of the payments", "tags": ["payments"], "fields": { "env": "prod", "host": "db02.example.test", "port": 1521 }, "levels": {} }
```

The request holds the record as the editor wants it at the levels that they hold. This is the partial edit of design 5.2:

- The header and every field at a level that the editor holds are as the request has them. A field left out has no value after the edit, but a `secret` left out keeps its value, as the page never has it; a `secret` with `null` or `""` is cleared.
- A value equal to the one that the record has is not checked again. So a `ref` to a record that has gone does not stop an edit of the other fields, and neither does a value that its template no longer allows.
- The levels are those of "Levels of a field", with the raises kept.
- The groups above the editor's level stay as they are, byte for byte, with their lists in `fields`. A field of theirs in the request is 400, also as `null`, as the editor cannot clear it, and so is the description when it is above. So the page sends only the fields of the levels that the editor holds.
- A field that would move across a level that the editor does not hold is 400: a change of `env` that puts a field of `levelByEnv` above the editor's level or brings one down from there, and a raise or a lowering (M5.8).
- A `secret` that the edit keeps goes into its new group as its group had it, never decoded (M5.8, kiler-crypto). A field that its group marks as a `secret` never goes to level 0, and a field that a group above the editor also lists is not written: both are 400, and only a file changed by hand has them.
- `createdAt` and `createdBy` never change, and Kiler sets `updatedAt` and `updatedBy`.
- The answer is 200 with the record.
- 400, with `field`: as for a new record, and the cases above. A request without `version` is 400 with `field` `null` (decided in M5.9).
- 404: the record is not there, its file does not fit (as for `GET /api/records/{id}`), or it has gone meanwhile, also right after the write, as for a new record. At a conflict on the share whose file there does not fit, the record has no `current` to give, so it is 404 too (decided in M5.9).
- 409: the conflict, with `current`; offline; the slot of another write; a template that cannot be used ("Record"): "The template of this record is not there, or cannot be used, so the record cannot be changed." (decided in M5.9); a group at a level of the editor that does not decrypt, as the edit would write over what it cannot read: "A part of this record cannot be read, so it cannot be changed. An admin restores it from the history."
- The conflict is found twice: against the version that this Kiler has, and against the share, where another Kiler may have written a version that no load has brought yet. Then `current` is the record on the share, read for this session (M5.8).

### `GET /api/stashes`

The stashes for the drop-down of a position of the path (design 6.6, "The form"): `GET /api/stashes?path=a1b2c3d4,e5f6g7h8` gives those for position 3, and no `path` those for position 1.

```json
{ "stashes": [ { "id": "e5f6g7h8", "name": "Prod", "used": true }, { "id": "j9k0m1n2", "name": "Test", "used": false } ] }
```

- Every stash that can be read. `used` are those that a record has at that position under that path. They come first, then the rest, each part by name. The page filters them as the user types.
- 400: a `path` that is not 0 to 3 ids.

### `POST /api/stashes`

A new stash, from the form of a record (design 6.6):

```json
{ "name": "Reports" }
```

- The name is trimmed and must not be blank.
- The answer is 201 with `{ "id": "p3q4r5s6", "name": "Reports" }`, the shape of "A stash of a path".
- 400, with `field` `name`: a blank name.
- 409: a stash of that name is there, without regard to case: "There is a stash of that name already." (decided in M5.9); the page reads the stashes again and offers it. Also offline, and the other outcomes of a write, whose sentence names the stash instead of the record.

### `GET /api/tags`

```json
{ "tags": ["payments", "reports"] }
```

The tags of the records, each once without regard to case, by name, for the suggestions of the form. The page filters them as the user types.

### `DELETE /api/records/{id}?version=<n>`

Moves the record to the trash over the version that the editor read (M2.5).

- An editor who holds the level of every group of the record (its `levels`) may delete it ("Decided in M5.1"). Another editor gets 403 with "A part of this record is at a level that you do not hold." (decided in M5.10). The layer deletes only that version, so the groups checked are those that go to the trash.
- The answer is 200 with the trash item, so the page can offer to restore it.
- 400: no `version`, with `field` `null`, as for an edit. A `version` that is not a whole number is the 400 of ASP.NET Core, without a body (decided in M5.10).
- 404: the record is not there, or its file does not fit (as for `GET /api/records/{id}`).
- 409: the conflict, with `current`; offline; the slot of another write; the other outcomes of a write.

### `GET /api/trash`

```json
{ "records": [] }
```

The trash items, by name without regard to case. A record whose file does not fit is left out, as the list leaves it out (decided in M5.10).

### `POST /api/trash/{id}/restore`

Brings a record back from the trash as it was deleted: a move, so its version, dates and authors stay (M2.5). The request has no body.

- An editor who holds the level of every group of the record may restore it ("Decided in M5.1"). Another editor gets 403 with the sentence of the delete.
- Kiler checks the right on the record in the trash as the last load gave it, the trash item of `GET /api/trash`. The layer moves the file of the trash without a version (M2.5), so another version that another Kiler deleted since that load is restored as it is, with its groups unchecked: it takes another Kiler a restore, an edit and a delete before a load of this Kiler sees them, some 20 s with the delay of the listings of the share. This limit is left for after the exam, with a version in the restore of the layer as its fix ([tasks/M5.md](tasks/M5.md), "Left for after the exam").
- The answer is 200 with the record.
- 404: no record of this id in the trash, or its file there does not fit; also a record that another Kiler has restored meanwhile: "The record is not in the trash. It may have been restored." (decided in M5.10).
- 409: offline; the slot of another write; the other outcomes of a write, such as a damaged file in the trash.

### `GET /api/records/{id}/history`

```json
{ "versions": [ { "version": 8, "updatedBy": { "account": "demo.editor", "name": "Demo Editor" }, "updatedAt": "2026-10-21T11:32:00Z", "levels": [1, 3] } ] }
```

- The versions of the record, the newest first, the current one with them: who changed it and when (design 5.6). A version whose file does not fit is left out.
- They come from the local copy, as every read does, also while offline (decided by the user in M5.11, until the exam; the versions of the share come after it, [tasks/M5.md](tasks/M5.md#left-for-after-the-exam)): the current version as `GET /api/records/{id}` has it, then the old versions that the copy keeps in its history. The copy keeps what it saw: the versions that the writes of this Kiler replaced, and those that other Kilers wrote after the version that it had when a load saw the change, up to 64 MiB and 20 000 old versions in all (M2.7a). A version from before the first load of the copy is not there, so a new local copy starts with the current versions only. A request reads the copy, so it waits for a load under way, up to some 60 s while a file server does not answer (`core.md`, "Calling the layer").
- 404: the record is not there, or its file does not fit (as for `GET /api/records/{id}`).

### `POST /api/records/{id}/history/{version}/restore`

Makes an old version the current one again:

```json
{ "version": 8 }
```

- `{version}` in the path is the old version, one of those that `GET /api/records/{id}/history` gives below the current one; `version` in the request is the current version that the editor read, and the restore is over it.
- The old version becomes the next version as it is, its header, its fields and its groups byte for byte, with `updatedAt` and `updatedBy` of the restore; `createdAt` and `createdBy` stay those of the current version (design 6.1). Kiler sets them before the write, or the check of design 7.8 would mark the record (M2.5). Nothing of it is checked again, as for a value that an edit keeps: a stash or a `ref` that has gone since stays.
- An editor who holds the level of every group of the current version and of the old one may restore it ("Decided in M5.1"). Another editor gets 403 with the sentence of the delete.
- The answer is 200 with the record.
- 400: no `version`, with `field` `null`, as for an edit. A `version` that is not a whole number is the 400 of ASP.NET Core, without a body.
- 404: the record is not there, or its file does not fit, as for an edit; an old version that the history does not give, also the current one and a `{version}` that is not a whole number: "That version of the record is not in its history." (decided in M5.11).
- 409: the conflict, with `current`; offline; the slot of another write; the other outcomes of a write.

## Decided in M5.1

Settled with the user, as the plan does not say:

- **Delete and restore.** An editor deletes a record, restores it from the trash and restores an old version of it only when they hold the level of every group of it; for an old version, of the current version and of the old one. A restore writes the groups of every level, so without this rule an editor of a lower level could bring back an old Prod password that they cannot read (design 7.4: no one changes a field above their level). An admin holds every level. The page hides the buttons by `levels` and the levels of the session.
- **"Show" and "copy"** of a `secret` are one request that says which it is (`use`), so the audit after the exam changes nothing in the API.
- **The 409 of a conflict** carries the record as it is now (`current`), so the page needs no other request, and the two versions side by side need no other answer.

## Not in this contract

What comes after the exam ([PLAN.md, section 10](PLAN.md#10-deferred-until-after-the-exam)):

- the private records, and the "Private only" filter (item 4);
- creating and changing a template (item 9);
- renaming, merging and deleting stashes, with the stash screen and its counters (item 10);
- the general settings (item 11). Until then the page gets guest mode from the session;
- the audit of Prod (item 1), which records the `use` of the request of a secret.

Not planned until the exam:

- the content of an old version, the "what" of design 5.6. M5.11 gives who changed the record and when; the content would come with the history of M6 step 6, which is done before the exam only if there is time (PLAN section 10);
- who deleted a record, and when, which nothing records: the trash item has the last change.

## The changes of other users

M6 step 9, decided in M6.11 (the event with the user, before the code):

- **The event.** The event stream `/api/events` sends `changed`, with no data, as it sends `session`: the page reads again what it shows, from the endpoints, which say only what its session may read. Kiler sends it after a load of the shared folder that gave a document new, changed, gone or changed outside Kiler, or a change of the trash; after a load that failed, and at each change of offline, as Kiler reads its records again then; and after each answer of success of a write of this Kiler, which no load gives (`POST` and `PUT /api/records`, `DELETE /api/records/{id}`, `POST /api/trash/{id}/restore`, `POST /api/records/{id}/history/{version}/restore` and `POST /api/stashes`), so the other pages of this Kiler see it too. A write of this Kiler gives a second `changed` at the next load (within about 10 s), which reads back the owner of the file it wrote; so does an admin action, which has no event of its own. Changes that come before the stream has sent the event of an earlier one give one event, after a `session` that comes with them.
- **The activity** (decided by the user). From a `changed` event until the user's next input in the page (a key, a pointer, a click, a text typed another way, or Back and Forward of the browser), the page sends each `GET` with the header `Kiler-Activity: none`, and Kiler does not count it as activity for the automatic lock; otherwise a page that the changes of others keep reading would never lock. Kiler takes the header only on a `GET`, so a write and the request of a secret are always activity; the header can only make the lock come sooner. A read that a click or a key starts counts as before.
