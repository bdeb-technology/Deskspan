## Current Status

- Version line is on `0.2.1`.
- Instant reconnect button exists in the paired card (`Reconnect` next to `Unpair`).
- Deep reconnect hardening added:
  - `NetworkNode` now lets non-preferred dialers attempt recovery after a short grace period (`5s`) when a link drops.
  - `ShareController` forces a reconnect attempt immediately on `LinkLost` when a peer is known.
- Public and private shared copies must stay in sync for:
  - `src/Deskspan.Net/NetworkNode.cs`
  - `src/Deskspan.App/ShareController.cs`
  - `src/Deskspan.App/ProfileStore.cs`
  - `src/Deskspan.App/SettingsWindow.xaml`
  - `src/Deskspan.App/SettingsWindow.xaml.cs`
  - `src/Deskspan.App/PairDirectory.cs`
- Validation checkpoint:
  - Free app Release build: pass.
  - Net tests (40): pass.
