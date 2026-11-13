# Free purple gather recovery, 2026-11-07

- Role `55290000000275`, game process `43296`; assistant session
  `c374477d74f446478cb1adb72eb893dd`, managed assembly
  `JSAssistantBridge_202206282622c716`.
- The fresh pre-run quote showed blue used 2/free 0 and purple used 0/free 1;
  the ordinary purple start quote was 0 money. Sequence 194 invoked start mode 2,
  but callback installation failed with `daily_timed_callback_lifetime_or_argument`.
  The assistant reported UNKNOWN and did not complete the gather.
- The subsequent game screen showed the actual purple gather board, so this was
  an applied start, not permission to send another start. The normal game UI was
  used to continue this existing board twice at 3,000 money each and then harvest.
- Observed directly in the game UI: purple inventory 395 → 449 (+54), money
  30,137,384 → 30,131,384 (−6,000), gold stayed 1,388. The game message also
  reported 54 purple souls; the post-harvest purple start button displayed a
  50,000-money price instead of free. The page was closed and the capital screen
  was visible afterward. No new gather start was submitted during recovery.
- After the second growth the eight numbers displayed on the purple balls were
  10, 6, 12, 10, 6, 3, 4 and 3 (sum 54), matching this harvest's 54 purple souls.
  The screen alone does not identify those display numbers with the raw level
  fields. This single observation does not establish the level limit or a general
  level-to-reward relationship.

The direct game recovery establishes this one business result. It does not
establish that the assistant's automatic callback, repeated-gather loop, or
other same-page flows work after their source fixes.
