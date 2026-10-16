# REAPER scroll compatibility

REAPER 7.78 has its own rounding rules for wheel packets. On this installation, 1-unit packets can move in the wrong direction vertically, and very small horizontal packets stop moving. REAPER also reads the physical Shift state when dispatching Shift+wheel.

Smoove detects REAPER's track area from its window class. It rounds cumulative output to safe wheel packet sizes while carrying the residue, so it preserves the requested wheel distance without sending reverse-prone 1-unit messages. For horizontal Shift+wheel, it sends a short Shift-down, wheel, Shift-up sequence for each delayed packet. This keeps the tail bound to REAPER's built-in Shift+wheel action after the user releases Shift. The original `reaper-kb.ini` binding was restored; no REAPER-specific keymap edit or backup file is needed.

The horizontal residue is eased over a bounded 120ms ending. New input cancels that ending and starts from the current motion. Both motion engines use the same output path. Other applications keep their existing wheel route.

The REAPER integration check requires one visible unsaved project. It injects one or three notches through SendInput, optionally releases Shift during the gesture, observes the output wheel messages on a separate hook thread, and records track-panel images. It restores the cursor and foreground when it finishes. `--reaper-analyze <absolute-output.csv> --verify` checks saved captures without sending input.

Live vertical captures cover both engines and directions at zoom offsets -20, 0, and +5, plus extra runs at -5. The additional tracks provided by the user let the test reach the minimum zoom. The captured single-notch runs preserved +/-240 output units and had no matched reverse frames. Before the fix, the same test showed reverse motion at the default scale and at minimum zoom.

Live horizontal captures cover both engines and directions at default zoom, with Shift released during the tail. Each single notch preserved +/-240 engine units. A three-notch burst preserved -720 units. The REAPER keymap remained at its original disabled Main HorizWheel binding throughout these final checks.

REAPER rounds wheel units to pixels, so equal wheel totals can render different distances depending on packet timing and zoom. Horizontal track grids are periodic, so image matching is reliable only when it finds an exact track landmark; unmatched frames are not used as proof of direction. Testing used synthetic wheel input. MIDI received separate reduced-distance routing and 60-unit output packets. Synthetic single-notch tests covered both engines; a short live UI check moved one note row at the current zoom. MIDI still scrolls discretely; physical-mouse animation and every MIDI zoom are not verified.
