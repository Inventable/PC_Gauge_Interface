# V3 Erase Safety and Performance Contract

This document is the firmware handoff for external-memory erase performance
and deployment safety. It is based on firmware branch
`codex/software-readiness-hil-entry` at commit `78923ac`.

## Confirmed Current Behaviour

The V3 firmware already retains the legacy EEPROM interlock:

- `EE_EXT_MEM_ERASE` is internal EEPROM address `0x0004`.
- `START_PROGRESS_ERASE` (64) writes `0x01` before starting block pair zero.
- `IDENTIFY` returns that byte as `erase_status`.
- `GET_ERASE_PROGRESS` (65) writes `0x00` only after it observes state
  `COMPLETE`.
- Power loss, a host disconnect, cancellation, and flash error leave the byte
  nonzero.

The interlock is therefore present, but two gaps remain in the firmware:

1. The main state machine does not block sensor start or recording when
   `erase_status` is nonzero.
2. `memServiceProgressErase()` is called only by command 65. The next block
   pair cannot start until another host request arrives.

## Source of the Erase Gaps

The application previously waited 150 ms between command-65 requests. Because
each request accounts for one completed pair and starts the next pair, this can
add up to 76.8 seconds of dead time across 512 pairs, with about 38.4 seconds
expected on average before serial processing overhead.

The supplied current trace supports this diagnosis. It shows an erase-active
window of approximately 226.7 ms, with successive active windows roughly
340-360 ms apart. The resulting approximately 110-140 ms quiet interval is
consistent with the old 150 ms host polling cadence rather than additional
flash erase time. The useful erase duty cycle in that trace is only about
two-thirds.

The application poll interval is now 20 ms. This bounds the same host-created
delay to 10.24 seconds and reduces its expected value to about 5.12 seconds.
This is an immediate compatibility improvement and requires no firmware
change. With the measured approximately 227 ms erase duration, the expected
pair cadence should fall from roughly 340-360 ms to approximately 247 ms,
subject to serial transaction overhead.

## Required Firmware Safety Changes

The firmware repository should implement all of the following:

1. Treat every `EE_EXT_MEM_ERASE` value other than `0x00` as unsafe.
2. On boot, read the interlock before entering `INIT_SENSOR`. While unsafe:
   - do not enter `INIT_SENSOR`, `RECORDING`, or `MEMORY_WRITE`;
   - do not create a V2 or V3 file, catalog reservation, header, sample, or
     footer;
   - keep the PC serial recovery path available.
3. Before issuing the first erase command, write `0x01` and read it back. Do
   not touch external flash if the readback is not `0x01`.
4. Clear the byte only after both flash devices report WIP clear, neither
   device reports an erase error, and the complete erase geometry has been
   accounted for.
5. Never clear the byte on timeout, reset, cancellation, communication loss,
   flash error, or an invalid progress state.
6. Progress completion must reset the RAM progress state to `IDLE`. Production
   V3 retires `END_MEM_ERASE` (53); command 65 reports completion and the
   application then verifies that `IDENTIFY.erase_status` is zero.

The lockout must be a firmware state-machine guard, not only an operator-facing
status byte. This prevents a gauge with stranded data from returning to a
deployable recording state when no PC is attached.

## Recommended Firmware Performance Change

Make progress erase autonomous after command 64:

- Call `memServiceProgressErase()` from the awake main loop or a dedicated
  erase state, not only from `GET_ERASE_PROGRESS`.
- As soon as both devices finish a pair, account for it and start the next pair
  without waiting for a serial request.
- Change command 65 to a status snapshot only; it must not be required to
  advance the erase.
- On autonomous completion, verify both status registers and clear the EEPROM
  interlock even if the host has disconnected.
- On autonomous error, latch the error state and leave the EEPROM interlock
  set.

The service loop should remain nonblocking so serial status requests continue
to work. A short service cadence (approximately 1-5 ms) removes the visible
current gaps without excessive status-register traffic.

## Restart-From-Zero Semantics

When `IDENTIFY.erase_status != 0`, the desktop application:

1. skips catalog/file-table reads and automatic downloads;
2. opens the erase recovery page immediately;
3. prevents dismissal into the normal file UI;
4. services the existing progress operation until it is no longer busy;
5. sends command 11, resetting the volatile progress state;
6. reconnects and verifies the same gauge identity. Command 65 may have
   legitimately cleared the old interlock if it observed completion during
   preparation, so the second identity check does not require it to remain
   nonzero;
7. sends command 64, which sets the interlock again and starts a new 512-pair
   V3 erase at address zero;
8. uses command 30 only if command 65/64 reports unsupported, which is the V2
   fallback;
9. after V3 completion, reads `IDENTIFY` directly; success is shown only when
   `erase_status` is confirmed as zero. The legacy V2 fallback retains command
   53.

The host sends command 64 only once. If the start acknowledgement is lost,
command 65 is the authoritative readback: `Busy` or `Complete` proves that the
original request was accepted, while any other state fails safely without a
second destructive start. Command 65 has a 2-second response timeout and a
7-second transaction deadline because the current implementation services the
flash erase before replying. The former one-second deadline could abandon a
healthy erase after it had already started, leaving the EEPROM interlock set.

This works with the current firmware, preserves V3 percentage reporting, and
does not resume a partially completed RAM session. A future command such as
`RESTART_PROGRESS_ERASE` (66) could perform the idle wait and progress-state
reset without rebooting. Do not silently change command 64 because existing
hosts interpret its busy response as a resumable session.

If any erase request stops receiving replies, the application abandons the
active view on the first transaction failure and returns to Disconnected.
It does not show Erase Complete or attempt to clear the interlock. Recovery is
offered after the gauge is discovered again.

## Required Firmware/HIL Evidence

- Measure the interval between WIP clearing and the next pair starting before
  and after autonomous servicing.
- Interrupt power after the EEPROM write and at the start, middle, and end of
  the 512-pair sequence; every reboot must report a nonzero erase status.
- Confirm that an unsafe gauge never starts the sensor or writes a V2/V3 byte.
- Inject chip-1, chip-2, and dual-chip errors and prove the flag remains set.
- Complete all 512 pairs with the host disconnected and prove the flag clears
  only after both devices report successful completion.
- Start a recovery while a pair is still busy and prove the subsequent erase
  begins from address zero.
- After command 65 reports completion, prove a new command 64 starts a new
  session at zero rather than resuming stale RAM progress.
