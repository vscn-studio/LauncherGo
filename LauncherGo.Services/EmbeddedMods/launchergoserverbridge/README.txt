LauncherGo Server Bridge

Author: VSCN-Studio
Copyright (C) 2026 HansJack, LauncherGo project owner (VSCN-Studio team)
License: MIT. See LICENSE.txt when this mod is distributed as a standalone
package, or the repository LICENSE file when distributed with LauncherGo.

This server-side mod is deployed by LauncherGo and listens only on 127.0.0.1. It uses the per-profile token in ModConfig/launchergoserverbridge.json.

Protocol version 2 supports authenticated queries, commands, and long-lived event subscriptions over NDJSON. OSQ HTTP clients are not compatible.
The bridge also exposes `players.history` and emits `player.count-changed`; player history is retained for seven days and only records changes in the online player count.
