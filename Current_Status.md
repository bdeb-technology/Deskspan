## Current Status

- Version line is on `0.2.3`.
- Fluency work:
  - Input reorder wait is `12 ms` (was `150 ms`).
  - Mouse moves stay on UDP whenever a UDP path exists; TCP is fallback only.
  - Keys fire on the capture thread instead of a thread-pool hop.
  - UDP receive/send buffers are `256 KB`; mouse and link loops run at highest thread priority.
- Instant reconnect button remains on the paired card.
- Validation checkpoint:
  - Free app Release build: pass.
  - Net tests (40): pass.
  - Input tests (6): pass.
