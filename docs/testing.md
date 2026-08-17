# Testing

## Milestone 1 - TCI connection diagnostic

Status: **PROVEN on Thetis**

Tested against Thetis TCI server at `ws://127.0.0.1:50001/`.

Observed initialization included:

- `protocol:ExpertSDR3,2.0;`
- `device:HERMES;`
- `receive_only:false;`
- `trx_count:2;`
- `tx_enable:0,true;`
- `trx:0,false;`
- `audio_samplerate:48000;`
- `audio_stream_sample_type:float32;`
- `audio_stream_channels:2;`
- `audio_stream_samples:2048;`
- `tx_stream_audio_buffering:50;`
- final `ready;`

The diagnostic detected `ready;` successfully.

### Important compatibility observations

The Thetis server is emulating TCI protocol 2.0 but advertises its current audio defaults as **48 kHz, float32, stereo, 2048 samples, 50 ms TX buffering**. The voice keyer should initially respect or explicitly negotiate these values rather than assuming int16 mono.

No PTT or audio was transmitted during this test.

## Next milestone

Milestone 2 will add a deliberately gated PTT-only test. It must:

1. Require an explicit command-line opt-in before transmitting any PTT command.
2. Verify `tx_enable:0,true;` before allowing the test.
3. Send `trx:0,true,tci;` for a very short controlled interval.
4. Always attempt `trx:0,false;` from a `finally` block.
5. Send no TX audio yet.
6. Log incoming `trx:` state changes and binary frame sizes for later TX_CHRONO analysis.
