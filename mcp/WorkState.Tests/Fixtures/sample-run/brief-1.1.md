# Task 1.1 — `A2`: raw file bytes are served by id with a tenant filter only

**Read `.claude/orchestration/security-idor-invite-checkout/CONVENTIONS.md` in full first. It is binding —
especially the red lines and the comment doctrine.** Branch is already checked out; do not create one.

## The defect

`StoredFileService.FindDocument` (`Nomos.Web/src/Files/StoredFileService.cs:47-52`) is
`_.Id == fileId && _.TenantId == tenantContext.TenantId` and nothing else. Any authenticated member of a firm
can iterate sequential integer file ids and download every colleague's case PDF, every library document and
every other user's private chat upload in the tenant.

`FileOwnerResolver.ResolveAsync(fileId)` (`Features/Files/Services/FileOwnerResolver.cs:41`) **already computes
which owner a file belongs to** — Case, Library or User, via `GetOwners` at `:174-179`. The download path
simply never asks it.

## The fix

**Put the read check on `IFileOwnerHandler` and make the two resolver methods that reach `FindDocument` ask it.**

### Where exactly — this is the part the spec originally got wrong, so read it carefully

Gate **`FileOwnerResolver.DownloadFile` (`:101`) and `FileOwnerResolver.ConvertFile` (`:109`)**.

Do **not** gate `ServeFileAsync` in `src/Files/Download/FileResponseExtensions.cs`. That extension method is a
thin wrapper over those two, and gating it would cover only the three controllers that happen to use the
wrapper while leaving every other caller of `DownloadFile`/`ConvertFile` open — including
`GestacDocumentHandler.Download`, which reaches `DownloadFile` directly and bypasses `ServeFileAsync` entirely.
Gating the resolver methods closes the three web routes **and** the partner route **and** every future caller,
which is the entire point of doing this here rather than at three call sites.

### Steps

1. **`Nomos.Web/src/Files/Abstructions/IFileOwnerHandler.cs`** (interface at `:20`) — add one member that answers
   "may the current caller read this file". A `Task<bool>` probe is preferable to a throwing member so the
   resolver decides the response shape in one place; name it for what it asks.

2. **`Features/Files/Services/Owners/CaseFileOwnerHandler.cs`** — implement it with `CaseAccessService`.
   **`caseAccess` is already injected at `:29`** (used at `:425` for `RequireDelete`), so no ctor change.
   Use **`GetCurrentUserPermissions(caseId).Read`** — the non-throwing probe at `CaseAccessService.cs:13`. It
   returns `CaseUserPermissions(false, false, false, false)` when there is no `CaseUsers` row, so absent
   membership is already fail-closed. **Do not add a new member to `CaseAccessService`; it has what you need.**

3. **`Features/Files/Services/Owners/LibraryFileOwnerHandler.cs`** — implement it as **tenant-scoped only:
   return true.** This is deliberate and is NOT an oversight. `LibraryUser` is a "my libraries" bookmark, not
   an ACL — membership enforcement was built once and withdrawn because users rejected it. The constraint is
   recorded on the `LibraryUser` entity; read that remark before you touch this. **Adding a membership check
   here is an explicit task failure.**

4. **`Features/Files/Services/Owners/UserFileOwnerHandler.cs`** — implement it as
   `tenantUserId == tenantContext.TenantUserId`. A user's private chat uploads are theirs.

5. **`Features/Files/Services/FileOwnerResolver.cs`** — in `DownloadFile` and `ConvertFile`, resolve the owner
   and consult the new member before returning bytes. A denial must be indistinguishable from "not found" to
   the caller — do not leak the file's existence.

## Constraints specific to this task

- **The Gestac partner path reaches `DownloadFile`.** `GET /api/gestac/documents/{id}/download` →
  `GestacDocumentHandler.Download` → `IFileOwnerResolver.DownloadFile`. It will now be checked. That is
  intended. It is expected to pass in practice because `GestacCaseService` grants every active tenant user
  `CaseUserPermission.Owner` on a synced case — but **do not rely on that, and do not special-case Gestac.**
  Write a test that a Gestac-shaped caller (a member with an Owner `CaseUsers` row) still downloads.
- **`ResolveAsync` throws `InvalidOperationException` when a file has no owner** (`:174`). Decide what a file
  with no owner row should do on a read path and say which you chose in `ASSUMPTIONS`. Fail closed.
- Do not change `CaseAccessService`, `ServeFileAsync`, `StoredFileService`, or any Gestac file.
- Do not change `FileOwnerResolver.Delete`, `Retry`, `SaveFile` or the upload paths. Read only.

## Done criteria

- A member with no `CaseUsers` row for case C cannot download, convert or otherwise obtain the bytes of a file
  attached to C, through any of: `GET /api/files/file/{id}`, `GET /api/cases/documents/{id}` file route,
  `GET /api/libraries/documents/{id}` file route, or `FileOwnerResolver.DownloadFile` called directly.
- A member **with** a `CaseUsers` row carrying Read still gets all of the above.
- A user file is readable by its owning `TenantUser` and by nobody else in the tenant.
- A library file remains readable by any member of the tenant.
- `dotnet build Nomos.slnx` clean.

## Verify

```powershell
dotnet build Nomos.slnx
dotnet test Nomos.Tests/Nomos.Tests.csproj --filter "FullyQualifiedName~File"
```

Then the full suite once, to prove nothing else regressed:
```powershell
dotnet test Nomos.Tests/Nomos.Tests.csproj
```

## Red-check — required

Write the negative tests first, confirm them **FAILING** against the current un-fixed code, record the failure
count and the assertion message, then implement and confirm green. Report both numbers.

Then name the wrong implementations your fixture would still pass — specifically, would it pass an
implementation that checks tenancy only? that checks the case but not the user file? that returns true for
every handler? Add the case that defeats each, or say why it cannot be written.

A DB-touching test carries `[RequiresDatabaseFact]`, never `[Fact]`.

## Report

Use the report format at the bottom of `CONVENTIONS.md`. No code, no diffs. `DEVIATIONS` is the line I care
most about — tell me anything in this brief that turned out factually wrong about the codebase.
