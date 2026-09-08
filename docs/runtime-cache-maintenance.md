# Runtime Cache Maintenance

Host caches and updater caches are maintained independently of UI refreshes.
Reading preferences never scans processes or waits for cache deletion. The
first workspace selection and updater service initialization enqueue work;
Host preparation and stop also request a pass. Requests are coalesced by root.

## Lifetime Protection

- A prepared Host directory has a `.lease-v1` marker and a `.use-lock` file.
  The launcher holds a shared read handle until the Host reports ready. The
  Host holds its own handle for its entire lifetime, including server restarts.
- Multiple instances of the same complete version share the same directory.
  Missing dependencies are rebuilt into another directory, never overwritten
  beneath another Host.
- Update downloads have a unique directory per attempt and the same shared
  lease. The updater script acquires its own handle and acknowledges ownership
  before the launcher may exit. Extraction stays inside that protected cache.
- Process termination releases handles automatically. Running copies are not
  retained as history; they remain only because they are still in use.

## Collection

A single asynchronous worker runs outside the UI and game threads. The first
pass is delayed ten seconds. A per-root file lock prevents multiple launcher
processes from collecting the same cache concurrently. A cross-process mutex serializes staging,
lease acquisition and directory isolation; cleanup never waits for that mutex.
Cleanup must acquire an exclusive use handle before isolation. On Windows,
the handle is closed during rename while the mutex still prevents acquisition
of a new lease. Deletion then runs outside the mutex in `.retired-*` directories.

Each pass has a two-second cooperative budget and yields for 50 ms after 16
file/directory operations. An individual OS file operation cannot be preempted.
Incomplete deletion resumes after a short delay; busy or inaccessible copies
are retried about every five minutes. A lifecycle request wakes the worker
with a ten-second debounce. With no pending work, the worker exits.

Recently prepared directories have a two-minute startup grace period. Old
Hosts without lease markers are inspected by Host process name only, once per
pass. Inaccessible process information prevents deletion. Legacy updater
directories also receive a one-day grace period and updater-process checks.
Directory links are not traversed by deletion.

No historical idle versions are deliberately retained. If the launcher exits
while Hosts continue running, collection resumes at the next launcher start.
No additional persistent cleanup process is installed.
