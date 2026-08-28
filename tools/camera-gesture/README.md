# Camera gesture test

`npm install && npm test`

The event camera's shutter carries three gestures on one button — tap for a photograph, hold for a
clip, slide onto the lock to leave the clip running — and which one happened is decided by a timer
and a handful of flags rather than by which handler fired. That state machine is the feature, and it
is the part that cannot be checked by holding a phone twice and hoping: the lock in particular has a
failure mode where releasing your finger stops the recording it has just locked, which looks like
"the lock does nothing" and is a one-line difference in `onShutterUp`.

There is no camera here. `getUserMedia`, `MediaRecorder`, `IndexedDB` and canvas are all stood in
for; `camera.js` itself is real and is what gets driven. Kept out of the .NET test project on
purpose — this needs a DOM, and the xUnit suite has no business growing a Node toolchain.
