# Design

## Context

F2 adds logical sources to the release 0.2 image builder. F1 supplies the storage URL and credential rules. F3 supplies the client download behavior, pre-init precedence for archive commands and sealed image interchange. The BACPAC itself is a SqlPackage input. [Research checked 2026-11-19](../../../docs/research/2026-11-19-bacpac-source-facts.md)

## Decisions

### D1. Use a user-installed SqlPackage

Both doctor and logical builds resolve an explicit executable path first or search PATH. Probe the selected executable with /Version. A missing executable yields exit 3, sqlpackage_missing and the installation command. Launch directly without a shell. Capture tool output privately and produce allowlisted diagnostics. SqlPackage exposes SQL passwords through argv for Import and Export. Version 170.5.96.0 rejects /Profile for both. Each import uses the restricted one-time login described in D3. Live Azure SQL export belongs to the unscheduled follow-up, where off-argv source authentication remains an open question. Alternatives considered were bundling DacFx and installing SqlPackage inside the engine. Either would transfer dependency management to SqlBranch.

### D2. Check inputs, engine and image before acquiring the source

Check command syntax, image name, local paths and readability, URL shape, region and required configuration before Docker. Read no package content and send no storage request in that step. Resolve the owned engine, acquire the image-name build lock, and check for an existing image under that lock. Resolve the tool before acquisition. Hold the lock through cleanup and publication. This avoids downloading an object for a name already taken or fetching twice under contention. Alternatives considered were validating package bytes before init, probing the object with HEAD early, and checking the name outside the lock. Each does work before the build can claim its destination or introduces a race.

Use the F3 client transport to stream the remote object into an exclusively created owner-only host temp directory (0700 directory and 0600 files on Unix, restrictive ACL on Windows). Keep SqlPackage scratch in the same protected scope. Close handles and stop and reap the child before deleting this operation’s files on success, failure or Ctrl+C. Verify ownership, path containment and file identity before deletion. On failed cleanup withhold publication and report a safe leftover identifier. A hard kill requires discovery of the abandoned operation. Preserve local user input and remote objects. The CLI docs must explain location, lifetime, cleanup and recovery. Direct streaming into a live import was considered and would make validation, digesting and capacity checks harder to verify.

### D3. Restrict import and publish the manifest last

Validate the staged package structure and inspect its model without executing it. Refuse a login-mapped database user with exit 1 and name the affected users. Check pool free bytes against the compressed BACPAC length immediately before creating the target and importing. Report pool_full if this lower bound fails or the pool subsequently fills. Report temp_full when the client temp filesystem fills. Database files, index builds and decompressed table data can exceed that lower bound.

Import into a fresh empty database with explicit files inside the locked build directory on the verified engine. After reserving the image name, create an operation-specific login in the reserved SqlBranch build namespace with a random one-time password and access confined to the import database. Pass only that password to SqlPackage argv. Keep the engine administrator password out of the child. Filter inherited credentials, capture and redact tool output, and suppress diagnostic files. Release the login on every terminal path and sweep it on recovery only after verifying build ownership and inactivity. Verify paths, engine identity and operation ownership before detach, drop or file deletion. On ambiguity preserve the affected resource and report it. Apply existing post-script restrictions, including enabled ownership-change trigger rejection. Reuse ImageSealer for recovery normalization, minimal log rebuild and immutable image files. Finish required cleanup before publication. The manifest is written last, as the image’s publication marker. Alternatives considered were importing as administrator, reusing a named database and stripping login-mapped users. Each would expand authority or change the supplied model.

### D4. Extend existing manifests and time the work

Add nullable provenance fields without changing archiveFormat 1. Preserve unknown manifest fields when F3 imports, renames or re-exports an image. Round-trip an unrelated extension field along with F2 provenance to detect lossy deserialization. Read existing manifests with absent fields. Measure attempted download, import, post-script, seal and cleanup phases with a monotonic clock, including the failing phase. Report the compressed byte count for the package. In JSON mode stream notices to stderr and emit one versioned result on stdout. Alternatives considered were introducing archiveFormat 2 and estimating elapsed time from source size. Neither is required for additive metadata or measured timings.

## Verification limits

Establish restricted-target authentication, package handling and protection of credentials outside the one-time argv exception on supported hosts before implementing the import path. Measure the 10 GB and 50 GB fixtures on a provisioned runner, recording runtime, hardware, tool version and peak disk use.
