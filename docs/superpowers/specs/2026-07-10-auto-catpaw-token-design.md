# Automatic Catpaw Token Design

## Goal

Add an optional automatic Token mode to the Thief Neko controller so a Catpaw
login Token rotation does not leave the gateway using stale credentials.

## User Interface

- Add an `自动获取 Catpaw Token` switch directly below the Token field.
- Keep the Token field available while automatic mode is enabled. Its saved
  value remains the fallback when Catpaw state cannot be read.
- Persist the switch with the existing encrypted controller settings.

## Startup Flow

1. Load the saved manual Token and automatic-mode setting.
2. Before each gateway start or save-and-restart, run the existing
   `src/catpawState.js` reader once.
3. Parse the reader output as one session result containing `token` and
   `userMis`. Continue passing `userMis` to the gateway as today.
4. When automatic mode is enabled and the reader returns a non-empty Token,
   use that Token for the new gateway process, update the Token field, and
   update the encrypted saved Token.
5. If automatic Token reading fails, keep using the saved manual Token and add
   a clear, sanitized warning to the activity list. Never log either Token.
6. Do not poll in the background and do not retry model requests. A Token
   change takes effect on the next start or restart.

## Compatibility

Existing settings files do not contain the new flag and therefore load with
automatic mode disabled. Manual Token behavior remains unchanged unless the
user explicitly enables the switch.

## Validation

- Settings tests cover backward-compatible loading and persistence of the new
  flag.
- Token-selection tests cover automatic success and fallback behavior without
  using real Catpaw credentials or inference quota.
- Build the WPF controller and run the existing Node test suite.
