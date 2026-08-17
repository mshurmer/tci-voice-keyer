# Milestone 3 - TX_CHRONO decode and silent TX audio

Goal: prove the Thetis TCI transmit-audio timing path without transmitting a voice recording or tone.

The diagnostic:

- waits for `ready;` and `tx_enable:0,true;`;
- explicitly negotiates 48 kHz, float32, stereo, 512 samples and 100 ms buffering;
- requires the operator to type exactly `SILENCE` before PTT;
- keys transceiver 0 using `trx:0,true,tci;` for about one second;
- decodes each binary 64-byte TCI stream header as little-endian uint32 fields;
- verifies stream type 3 (`TX_CHRONO`);
- constructs a matching type 2 (`TX_AUDIO_STREAM`) packet with a zero-filled sample payload;
- unkeys with `trx:0,false;` and waits for `trx:0,false;` confirmation.

The TCI Stream header fields used are receiver, sample_rate, format, codec, crc, length, type, channels and eight reserved uint32 values. The header is 64 bytes.

For Thetis, source inspection confirms that the stream header is written little-endian and that modern TX_CHRONO length semantics use scalar sample count (`samples * channels`) after the client sends modern audio negotiation commands. The diagnostic therefore sends those negotiation commands before PTT and sizes the silent payload from the TX_CHRONO `length` field.

Expected live output should include lines similar to:

```text
BINARY type=3 receiver=0 rate=48000 sample=float32 length=1024 channels=2 bytes=64
  -> TX_CHRONO #1: sent silent TX_AUDIO_STREAM (4160 bytes)
```

For float32 stereo with 512 samples/channel, `length=1024` scalar samples and the packet size is 64-byte header + 1024 * 4 bytes = 4160 bytes.

Safety: if any result is unexpected, manually ensure MOX is off and do not repeat the test until the output has been reviewed.
