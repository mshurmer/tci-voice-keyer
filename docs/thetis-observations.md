# Thetis TCI observations

Observed during the first successful live connection test:

- Server identifies as `protocol:ExpertSDR3,2.0;`.
- Device identifies as `HERMES`.
- Two transceivers are reported.
- Transceiver 0 has TX permission: `tx_enable:0,true;`.
- Transceiver 1 has TX disabled: `tx_enable:1,false;`.
- Initial PTT state is RX: `trx:0,false;` and `trx:1,false;`.
- Audio sample rate: 48000 Hz.
- Audio stream sample type: float32.
- Audio stream channels: 2.
- Audio stream samples: 2048.
- TX stream buffering: 50 ms.
- Initialization completes with `ready;`.

These observations are from Thetis rather than assumptions from the generic TCI protocol specification and should guide the first compatibility implementation.
