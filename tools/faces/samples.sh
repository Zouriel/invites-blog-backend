#!/usr/bin/env bash
# Public-domain portraits from Wikimedia Commons (NASA and White House photos) for FaceAnalyzerTests.
set -euo pipefail
dir="${1:?folder}"
mkdir -p "$dir"
get() { curl -sSLf -A "invites-blog-face-tests/1.0" -o "$dir/$1.jpg" "https://commons.wikimedia.org/wiki/Special:FilePath/$2?width=${W:-1600}"; }
get collins1 "Commander%20Eileen%20Collins%20-%20GPN-2000-001177.jpg"
get collins2 "Eileen%20Collins%2C%20early%20NASA%20portrait.jpg"
get obama1 "President%20Barack%20Obama.jpg"
get obama2 "Official%20portrait%20of%20Barack%20Obama.jpg"
get family "Barack%20Obama%20family%20portrait%202011.jpg"
get cabinet "President%20Barack%20Obama%20with%20full%20cabinet%2009-10-09.jpg"
get kelly "Scott%20J.%20Kelly.jpg"
W=2400 get crowd "White%20House%20event%20on%20the%20chemical%20weapons%20convention%20treaty%2060078u.jpg"
