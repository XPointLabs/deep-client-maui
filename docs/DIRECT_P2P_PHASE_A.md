# DirectP2P Android radio Phase A

This phase adds capability, permission, and bounded-discovery scaffolding only.
It is not a DirectP2P message transport and is not evidence that nearby message
delivery works.

## Safety boundary

- Production composition does not register the Android permission broker,
  capability probe, discovery adapter, or an
  `IDirectP2pSessionMessageTransport`.
- The Android discovery adapter reports hardware and permission readiness but
  always sets protocol activation to false and rejects every open request.
- No native scan, advertisement, peer connection, identity, account data,
  message data, attachment data, or cryptographic handshake is implemented.
- The only future discovery value admitted by the portable boundary is a
  fixed-size immutable 32-byte opaque hint. It has no identity or message
  semantics. Rotation, authentication, replay handling, and unlinkability must
  be specified by the reviewed protocol before a native adapter may emit it.
- Discovery requests are limited to 30 seconds and 64 observations. Adapter
  open, completion, stop, and disposal waits are bounded. A cancelled adapter
  open is contractually failure-atomic and must retain no native radio state.

## Android permission split

- Android 12/API 31 and later BLE uses `BLUETOOTH_SCAN`,
  `BLUETOOTH_CONNECT`, and `BLUETOOTH_ADVERTISE`; Android 11/API 30 and earlier
  uses fine location for BLE discovery.
- Android 13/API 33 and later Wi-Fi Direct/Aware uses
  `NEARBY_WIFI_DEVICES`; Android 12L/API 32 and earlier uses fine location.
- Legacy Bluetooth manifest permissions stop at API 30 and legacy fine
  location stops at API 32. BLE scan and nearby Wi-Fi declarations use
  `neverForLocation`; this scaffold has no location-derived behavior.
- BLE, Wi-Fi Direct, and Wi-Fi Aware hardware features are optional, so adding
  the declarations cannot exclude otherwise supported devices.

The runtime broker requests only the SDK-specific permission set for the
selected radio. It is internal and unregistered, so this phase cannot display a
permission prompt in the product.

## Activation blockers

Native discovery and link establishment remain blocked until the project has
an accepted authenticated key exchange and record protocol, privacy-safe
rotating discovery hints, device authorization/revocation, durable delivery
semantics, battery/thermal evidence, and two-device Android E2E coverage.
