# NZBDAV managed-file operation audit

`NzbdavLibrarySyncTask` was audited for recovery, quarantine, tombstone, marker,
probe, and `.strm` artifact path operations.

- Linux managed reads, existence checks, identity checks, directory creation,
  writes, publication, quarantine copies, and tombstones enter through a
  canonical library-root descriptor. `openat2` uses
  `RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS | RESOLVE_NO_XDEV`; `fstat` identity
  and bounded SHA-256 checks are retained through each commit/post-check.
- Linux `.strm` enumeration is rooted at a held `/proc/self/fd/<root>`
  descriptor. Enumerated names are only candidates; the subsequent read still
  uses the rooted `openat2` path.
- Linux does not use `File.Exists`, `Directory.Exists`, `File.GetAttributes`,
  path-based recursive enumeration, or path-based rollback for managed state.
  The remaining `ContainsSymlinkInPath` Linux branch is intentionally a no-op;
  descriptor resolution is the safety proof, not a pathname pre-check.
- Windows `File.Exists`/`Directory.Exists`, `Directory.CreateDirectory`, and
  `FileStream` calls are confined to Windows/non-Linux branches. Existing
  destinations are opened and identity-checked in place; fresh publication is
  always no-replace. No Windows transaction path-replaces a destination after
  validation.
- Generic non-Linux directory-sync and preparation cleanup calls are likewise
  platform branches and are not used by Linux managed publication.

Matching-byte quarantine destinations are accepted only when the durable copied
v4 managed marker is present, byte-identical to the current source marker, and
binds the same item, operation, stream hash, and (when claimed) probe hash.
Missing, malformed, or foreign markers fail closed. A durable tombstone is
written after all copies and is required for reconciliation/ETag convergence.
