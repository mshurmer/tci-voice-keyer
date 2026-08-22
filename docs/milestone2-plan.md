# Milestone 2 - Controlled PTT diagnostic

Milestone 1 has proven that Thetis accepts the WebSocket connection and completes TCI initialization.

Milestone 2 will test PTT only. It will not transmit voice audio.

Safety requirements:

- explicit `--ptt-test` opt-in;
- wait until `ready;` is received;
- require `tx_enable:0,true;`;
- three-second warning before keying;
- key transceiver 0 with `trx:0,true,tci;`;
- hold PTT for approximately one second only;
- explicitly unkey with `trx:0,false;`;
- attempt the unkey command again from fail-safe cleanup if the normal sequence is interrupted;
- log TCI state changes and binary frame sizes.

The purpose is to prove TCI PTT and observe whether Thetis begins sending TX timing/binary traffic before implementing TX audio.
