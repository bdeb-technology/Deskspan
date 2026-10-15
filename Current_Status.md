## Current Status

- Version line is on `0.3.0`.
- Stability work (never drop unless the user unpairs):
  - Session TCP sockets now use keepalive (`SocketTuning`): idle 10s, probe every 2s.
  - Link receive timeout raised to `12 s` (heartbeat stays `250 ms`), so jitter no longer kills the link.
  - Dropped links reconnect fast and silently: dial loop retries every `300 ms` right after a drop, both ends dial after a `2 s` grace, and the status reads "Reconnecting to ..." instead of "disconnected".
  - A watchdog keeps hunting on the LAN and keeps a waiter parked at the relay whenever a peer is remembered and the link is down.
  - A network-adapter change only re-searches; it never drops a live link.
  - Wake from sleep reconnects on its own.
- Startup:
  - "Start with Windows" (default on) registers a per-user Run entry and opens Deskspan minimized to the tray at login, then reconnects to the paired PC automatically.
  - Toggle lives in the Settings "Startup" card.
- Fluency (unchanged from 0.2.3, with one correction):
  - Mouse moves prefer UDP; keys fire on the capture thread; UDP buffers `256 KB`; loops at highest priority.
  - Input reorder wait is `80 ms` so the reliable TCP copy can fill a lost UDP packet before flushing (only adds latency when a packet is actually missing); delivery is back inside the lock to keep strict order across the UDP and TCP paths.
- Validation checkpoint:
  - Free Release build: pass. Pro Release build: pass.
  - Net tests (40), Input tests (6), Pro tests (15): pass.
